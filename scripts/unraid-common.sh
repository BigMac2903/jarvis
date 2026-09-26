#!/usr/bin/env bash
# Shared functions. Sourcing this file never starts Docker or changes the host.
jarvis_fail() { printf 'JARVIS: %s\n' "$*" >&2; return 1; }

jarvis_private_ipv4() {
  local ip="$1" a b c d part
  [[ "$ip" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]] || return 1
  IFS=. read -r a b c d <<< "$ip"
  for part in "$a" "$b" "$c" "$d"; do
    [[ "$part" == 0 || "$part" != 0* ]] || return 1
    ((10#$part <= 255)) || return 1
  done
  ((a == 10 || (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168)))
}

jarvis_env_get() {
  local file="$1" key="$2"
  [[ -f "$file" ]] || return 0
  awk -v key="$key" 'index($0,key "=")==1 {sub(/\r$/, ""); print substr($0,length(key)+2); exit}' "$file"
}

jarvis_env_set() {
  local file="$1" key="$2" value="$3" temporary
  [[ ! -L "$file" ]] || { jarvis_fail 'Die .env darf kein symbolischer Link sein.'; return 1; }
  [[ "$key" =~ ^[A-Z_]+$ && "$value" != *$'\n'* && "$value" != *$'\r'* ]] || return 1
  temporary="$(mktemp "${file}.tmp.XXXXXX")" || return 1
  # ENVIRON preserves literal backslashes; never evaluate .env as shell code.
  if ! JARVIS_VALUE="$value" awk -v key="$key" '
    BEGIN {value=ENVIRON["JARVIS_VALUE"]}
    index($0,key "=")==1 {if (!found) print key "=" value; found=1; next}
    {print}
    END {if (!found) print key "=" value}' "$file" > "$temporary"; then
    jarvis_fail "Konfiguration konnte nicht geschrieben werden: $temporary"; return 1
  fi
  chmod 600 "$temporary" && mv -f -- "$temporary" "$file"
}

jarvis_detect_address() {
  local ip
  while IFS= read -r ip; do
    if jarvis_private_ipv4 "$ip"; then printf '%s\n' "$ip"; return 0; fi
  done < <(ip -o -4 addr show scope global | awk '
    $2 ~ /^(br[0-9]+|bond[0-9]+|eth[0-9]+|en[a-z0-9]+)(@[^ ]+)?$/ {
      split($4,a,"/"); if ($2 ~ /^br0/) print "0 " a[1]; else print "1 " a[1]
    }' | sort -s -k1,1 | cut -d' ' -f2)
  jarvis_fail 'Keine private Unraid-IPv4 erkannt. Mit --address DEINE-UNRAID-IP erneut starten.'
}

jarvis_choose_port() {
  local first="$1" sockets="$2" published="$3" port
  for ((port=first; port<first+100; port++)); do
    # Also inspect Docker mappings: they may use NAT without a listening proxy.
    if printf '%s\n' "$sockets" | awk '{print $4}' | grep -Eq ":${port}$"; then continue; fi
    if printf '%s\n' "$published" | awk -v port="$port" '
      {count=split($0,bindings,/[ ,]+/); for(i=1;i<=count;i++) {
        binding=bindings[i]; if(index(binding,"->")==0) continue;
        sub(/->.*/,"",binding); sub(/^.*:/,"",binding);
        n=split(binding,bounds,"-");
        if(bounds[1] ~ /^[0-9]+$/ && ((n==1 && port==bounds[1]) || (n==2 && port>=bounds[1] && port<=bounds[2]))) found=1;
      }} END {exit(found ? 0 : 1)}'; then continue; fi
    printf '%s\n' "$port"; return 0
  done
  jarvis_fail "Keine freien Ports ab $first gefunden."
}

jarvis_compose() {
  local root="$1"; shift
  if docker --host unix:///var/run/docker.sock compose version >/dev/null 2>&1; then
    docker --host unix:///var/run/docker.sock compose "$@"
  elif [[ -x "$root/tools/docker-cli/cli-plugins/docker-compose" ]]; then
    docker --host unix:///var/run/docker.sock --config "$root/tools/docker-cli" compose "$@"
  else
    jarvis_fail 'Compose fehlt. Zuerst install-unraid.sh ausführen.'
  fi
}

jarvis_install_compose() {
  local root="$1" version=v5.5.1 digest arch target download actual
  if docker --host unix:///var/run/docker.sock compose version >/dev/null 2>&1; then return 0; fi
  case "$(uname -m)" in
    x86_64) arch=x86_64; digest=db1889184726840f75c4f9c001048430d4f25b3be3cb084d3ddd762bc0aed576 ;;
    *) jarvis_fail 'Dieser Unraid-Installer benötigt x86_64.'; return 1 ;;
  esac
  target="$root/tools/docker-cli/cli-plugins/docker-compose"
  mkdir -p "$(dirname "$target")"
  if [[ -f "$target" ]]; then
    actual="$(sha256sum "$target" | awk '{print $1}')"
    [[ "$actual" == "$digest" ]] || { jarvis_fail 'Vorhandene private Compose-Datei hat eine andere Prüfsumme. Nicht überschrieben.'; return 1; }
  else
    download="$(mktemp "${target}.download.XXXXXX")"
    printf 'Lade Docker Compose %s ...\n' "$version"
    curl --fail --show-error --silent --location --proto '=https' --proto-redir '=https' --connect-timeout 20 --max-time 300 \
      "https://github.com/docker/compose/releases/download/$version/docker-compose-linux-$arch" -o "$download" || return 1
    actual="$(sha256sum "$download" | awk '{print $1}')"
    [[ "$actual" == "$digest" ]] || { jarvis_fail "Compose-Prüfsumme falsch; Datei wird nicht ausgeführt: $download"; return 1; }
    chmod 755 "$download" && mv -- "$download" "$target" || return 1
  fi
  jarvis_compose "$root" version
}
