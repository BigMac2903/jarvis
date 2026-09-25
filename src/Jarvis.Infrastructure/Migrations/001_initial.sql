CREATE TABLE IF NOT EXISTS users (
 id text PRIMARY KEY, username text NOT NULL UNIQUE, password_hash text NOT NULL,
 email text NOT NULL, timezone text NOT NULL, language text NOT NULL,
 totp_secret text, totp_pending text, totp_last_step bigint NOT NULL DEFAULT -1,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS sessions (
 token_hash text PRIMARY KEY, user_id text NOT NULL REFERENCES users(id) ON DELETE CASCADE,
 csrf text NOT NULL, expires_at timestamptz NOT NULL, absolute_expires_at timestamptz NOT NULL
);
CREATE TABLE IF NOT EXISTS documents (
 owner text NOT NULL, kind text NOT NULL, id text NOT NULL, data jsonb NOT NULL,
 updated_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(owner,kind,id)
);
CREATE INDEX IF NOT EXISTS documents_recent ON documents(owner,kind,updated_at DESC);
CREATE TABLE IF NOT EXISTS approvals (
 id text PRIMARY KEY, owner text NOT NULL, tool text NOT NULL, args jsonb NOT NULL,
 state text NOT NULL DEFAULT 'pending', expires_at timestamptz NOT NULL DEFAULT now()+interval '10 minutes',
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS audit (
 id bigserial PRIMARY KEY, owner text NOT NULL, tool text NOT NULL, status text NOT NULL,
 duration_ms bigint NOT NULL, approval text, parameters jsonb NOT NULL, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS device_tokens (
 hash text PRIMARY KEY, owner text NOT NULL, device_id text NOT NULL, revoked boolean NOT NULL DEFAULT false
);
CREATE TABLE IF NOT EXISTS pairings (
 token_hash text PRIMARY KEY, owner text NOT NULL, code_hash text NOT NULL,
 expires_at timestamptz NOT NULL, used boolean NOT NULL DEFAULT false
);
CREATE TABLE IF NOT EXISTS commands (
 id text PRIMARY KEY, owner text NOT NULL, device_id text NOT NULL, command text NOT NULL,
 args jsonb NOT NULL, state text NOT NULL DEFAULT 'pending', result jsonb,
 created_at timestamptz NOT NULL DEFAULT now(), expires_at timestamptz NOT NULL DEFAULT now()+interval '2 minutes'
);
CREATE TABLE IF NOT EXISTS oauth_states (
 state_hash text PRIMARY KEY, owner text NOT NULL, provider text NOT NULL, verifier text NOT NULL,
 expires_at timestamptz NOT NULL DEFAULT now()+interval '10 minutes'
);
