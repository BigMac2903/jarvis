using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
namespace Jarvis.Windows;

internal static class Program
{
    [STAThread]
    static void Main(){ApplicationConfiguration.Initialize();Application.Run(new AgentForm());}
}
internal sealed class AgentForm : Form
{
    readonly TextBox server=new(){PlaceholderText="https://jarvis.example.com",Width=420};
    readonly TextBox pairToken=new(){PlaceholderText="Pairing-Token",UseSystemPasswordChar=true,Width=420};
    readonly TextBox code=new(){PlaceholderText="6-stelliger Pairing-Code",Width=420};
    readonly CheckBox consent=new(){Text="Bildschirm und UI-Steuerung für diese Sitzung freigeben",AutoSize=true};
    readonly Label status=new(){Text="Nicht verbunden",AutoSize=true};
    readonly HttpClient client=new(){Timeout=TimeSpan.FromSeconds(25)};
    readonly CancellationTokenSource stop=new();
    readonly NotifyIcon tray;
    readonly Dictionary<string,(AutomationElement Element,DateTimeOffset Expires)> elements=new();
    readonly string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Jarvis");
    AgentConfig config=new();
    bool started;
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd,StringBuilder text,int maxCount);

    public AgentForm()
    {
        Text="JARVIS Geräte-Agent";Width=500;Height=430;MinimumSize=new Size(500,430);
        var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,Padding=new Padding(20),WrapContents=false,AutoScroll=true};
        var pair=new Button{Text="Gerät verbinden",AutoSize=true};
        pair.Click+=async(_,_)=>await Pair();
        var configure=new Button{Text="Lokale Freigaben öffnen",AutoSize=true};
        configure.Click+=(_,_)=>{Directory.CreateDirectory(folder);var path=Path.Combine(folder,"config.json");if(!File.Exists(path))File.WriteAllText(path,JsonSerializer.Serialize(config,new JsonSerializerOptions{WriteIndented=true}));Process.Start(new ProcessStartInfo("notepad.exe",path){UseShellExecute=false});};
        panel.Controls.AddRange([new Label{Text="JARVIS · Windows",AutoSize=true,Font=new Font(Font.FontFamily,17)},server,pairToken,code,pair,consent,configure,status]);
        Controls.Add(panel);
        tray=new NotifyIcon{Icon=SystemIcons.Application,Text="JARVIS — Verbindung nur ausgehend",Visible=true};
        var menu=new ContextMenuStrip();menu.Items.Add("Öffnen",null,(_,_)=>{Show();WindowState=FormWindowState.Normal;});
        menu.Items.Add("Zugriff stoppen",null,(_,_)=>consent.Checked=false);
        menu.Items.Add("Beenden",null,(_,_)=>Close());tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,_)=>Show();
        Resize+=(_,_)=>{if(WindowState==FormWindowState.Minimized)Hide();};
        FormClosing+=(_,_)=>{stop.Cancel();tray.Dispose();client.Dispose();};
        Shown+=async(_,_)=>{try{LoadConfig();if(File.Exists(Path.Combine(folder,"token.bin"))){var token=Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(folder,"token.bin")),null,DataProtectionScope.CurrentUser));Connect(token);await Run();}}catch(Exception e){status.Text=e.Message;}};
    }
    void LoadConfig()
    {
        var path=Path.Combine(folder,"config.json");
        if(File.Exists(path))config=JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path))??new();
        server.Text=config.Server;
    }
    void Connect(string token)
    {
        var uri=new Uri(config.Server);
        if(uri.Scheme!="https")throw new InvalidOperationException("HTTPS mit vertrauenswürdigem Zertifikat erforderlich.");
        client.BaseAddress=new Uri(config.Server.TrimEnd('/')+"/api/v1/");
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
    }
    async Task Pair()
    {
        try{
            if(started)throw new InvalidOperationException("Zum erneuten Pairing Agent neu starten.");
            config.Server=server.Text.Trim();var uri=new Uri(config.Server);if(uri.Scheme!="https")throw new InvalidOperationException("HTTPS erforderlich.");
            using var request=new HttpClient();
            using var response=await request.PostAsJsonAsync(config.Server.TrimEnd('/')+"/api/v1/devices/pair",new{token=pairToken.Text,code=code.Text,name=Environment.MachineName,platform="windows"});
            response.EnsureSuccessStatusCode();var result=(await response.Content.ReadFromJsonAsync<JsonObject>())!;
            var token=result["token"]!.GetValue<string>();Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder,"token.bin"),ProtectedData.Protect(Encoding.UTF8.GetBytes(token),null,DataProtectionScope.CurrentUser));
            File.WriteAllText(Path.Combine(folder,"config.json"),JsonSerializer.Serialize(config,new JsonSerializerOptions{WriteIndented=true}));
            pairToken.Clear();code.Clear();Connect(token);await Run();
        }catch(Exception e){status.Text="Verbindung fehlgeschlagen: "+e.GetType().Name;}
    }
    async Task Run()
    {
        if(started)return;started=true;
        while(!stop.IsCancellationRequested)
        {
            try{
                var screen=Screen.PrimaryScreen?.Bounds??Rectangle.Empty;var title=new StringBuilder(512);GetWindowText(GetForegroundWindow(),title,512);
                using var heartbeat=await client.PostAsJsonAsync("device-agent/status",new{computerName=Environment.MachineName,resolution=$"{screen.Width}x{screen.Height}",activeApplication=consent.Checked?title.ToString():"nicht freigegeben",online=true},stop.Token);
                heartbeat.EnsureSuccessStatusCode();
                var commands=await client.GetFromJsonAsync<JsonArray>("device-agent/commands",stop.Token)??[];
                status.Text="Verbunden · "+DateTime.Now.ToLongTimeString();
                foreach(var command in commands)
                {
                    JsonObject result;
                    try{
                        if(DateTimeOffset.Parse(command!["expires_at"]!.GetValue<string>())<DateTimeOffset.UtcNow)throw new InvalidOperationException("Abgelaufen.");
                        var args=JsonNode.Parse(command["args"]!.GetValue<string>())!.AsObject();
                        result=Execute(command["command"]!.GetValue<string>(),args);result["ok"]=true;
                    }catch(Exception e){result=new(){["ok"]=false,["error"]=e is InvalidOperationException?e.Message:"Geräteaktion fehlgeschlagen."};}
                    using var posted=await client.PostAsJsonAsync("device-agent/commands/"+command!["id"]!.GetValue<string>()+"/result",result,stop.Token);
                    posted.EnsureSuccessStatusCode();
                }
            }catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch(Exception e){status.Text="Verbindung unterbrochen · "+e.GetType().Name;}
            try{await Task.Delay(3000,stop.Token);}catch(OperationCanceledException){break;}
        }
    }
    JsonObject Execute(string command,JsonObject args)
    {
        if(command=="GetInfo")return new(){["computerName"]=Environment.MachineName,["os"]=Environment.OSVersion.ToString(),["userInteractive"]=Environment.UserInteractive,["screenConsent"]=consent.Checked};
        if(command=="ListApplications"){
            var installed=new List<string>();
            foreach(var root in new[]{Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)})
                if(Directory.Exists(root))installed.AddRange(Directory.EnumerateFiles(root,"*.lnk",SearchOption.AllDirectories).Take(500).Select(Path.GetFileNameWithoutExtension).OfType<string>());
            return new(){["allowed"]=JsonSerializer.SerializeToNode(config.Applications.Keys),["discovered"]=JsonSerializer.SerializeToNode(installed)};
        }
        if(!consent.Checked)throw new InvalidOperationException("Lokale Bildschirm-/Steuerungsfreigabe fehlt.");
        switch(command)
        {
            case "TakeScreenshot":
                var bounds=Screen.PrimaryScreen?.Bounds??throw new InvalidOperationException("Kein Bildschirm.");
                using(var bitmap=new Bitmap(bounds.Width,bounds.Height)){using(var graphics=Graphics.FromImage(bitmap))graphics.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size);using var output=new MemoryStream();bitmap.Save(output,ImageFormat.Png);return new(){["image"]="data:image/png;base64,"+Convert.ToBase64String(output.ToArray())};}
            case "GetElements":
                elements.Clear();var root=AutomationElement.FromHandle(GetForegroundWindow());var found=root.FindAll(TreeScope.Descendants,Condition.TrueCondition);var list=new JsonArray();
                for(var i=0;i<Math.Min(found.Count,300);i++){var element=found[i];if(element.Current.IsPassword||element.Current.IsOffscreen)continue;var id=Guid.NewGuid().ToString("N");elements[id]=(element,DateTimeOffset.UtcNow.AddSeconds(45));list.Add(new JsonObject{["id"]=id,["name"]=element.Current.Name,["type"]=element.Current.ControlType.ProgrammaticName,["enabled"]=element.Current.IsEnabled});}
                return new(){["elements"]=list};
            case "ClickElement":
            case "TypeText":
                var key=args["elementId"]!.GetValue<string>();
                if(!elements.TryGetValue(key,out var match)||match.Expires<DateTimeOffset.UtcNow||match.Element.Current.IsPassword)throw new InvalidOperationException("UI-Element ungültig oder veraltet.");
                if(command=="TypeText"){
                    if(!match.Element.TryGetCurrentPattern(ValuePattern.Pattern,out var pattern)||((ValuePattern)pattern).Current.IsReadOnly)throw new InvalidOperationException("Feld nicht beschreibbar.");
                    ((ValuePattern)pattern).SetValue(args["text"]!.GetValue<string>());
                }else{
                    if(!match.Element.TryGetCurrentPattern(InvokePattern.Pattern,out var pattern))throw new InvalidOperationException("Element unterstützt kein Invoke.");
                    ((InvokePattern)pattern).Invoke();
                }return new();
            case "PressKey":
                var keys=new Dictionary<string,string>{{"ENTER","{ENTER}"},{"ESC","{ESC}"},{"TAB","{TAB}"},{"UP","{UP}"},{"DOWN","{DOWN}"},{"LEFT","{LEFT}"},{"RIGHT","{RIGHT}"},{"CTRL+S","^s"}};
                if(!keys.TryGetValue(args["key"]!.GetValue<string>(),out var send))throw new InvalidOperationException("Taste nicht erlaubt.");SendKeys.SendWait(send);return new();
            case "OpenApplication":
            case "CloseApplication":
                var app=args["application"]!.GetValue<string>();if(!config.Applications.TryGetValue(app,out var executable)||!Path.IsPathFullyQualified(executable)||!File.Exists(executable))throw new InvalidOperationException("Anwendung lokal nicht freigegeben.");
                var baseName=Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
                if(new[]{"cmd","powershell","pwsh","wscript","cscript","mshta","rundll32","regsvr32","bash","sh","python","node"}.Contains(baseName))throw new InvalidOperationException("Interpreter und Shells sind gesperrt.");
                if(command=="OpenApplication")Process.Start(new ProcessStartInfo(executable){UseShellExecute=false});
                else foreach(var process in Process.GetProcessesByName(baseName)){try{if(string.Equals(process.MainModule?.FileName,executable,StringComparison.OrdinalIgnoreCase))process.CloseMainWindow();}catch(System.ComponentModel.Win32Exception){}}
                return new();
            case "OpenFile":
            case "OpenFolder":
                var path=Path.GetFullPath(args["path"]!.GetValue<string>());
                if(!config.AllowedFolders.Any(root=>path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Pfad nicht freigegeben.");
                var current=path;while(!string.IsNullOrEmpty(current)){if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Links nicht erlaubt.");current=Path.GetDirectoryName(current)??"";}
                if(command=="OpenFile"&&!new[]{".txt",".md",".pdf",".png",".jpg",".jpeg",".docx",".xlsx"}.Contains(Path.GetExtension(path).ToLowerInvariant()))throw new InvalidOperationException("Dateityp gesperrt.");
                if(command=="OpenFolder"&&!Directory.Exists(path))throw new InvalidOperationException("Ordner fehlt.");
                Process.Start(new ProcessStartInfo(path){UseShellExecute=true});return new();
            default:throw new InvalidOperationException("Unbekanntes Kommando.");
        }
    }
}
internal sealed class AgentConfig
{
    public string Server{get;set;}="";
    public Dictionary<string,string> Applications{get;set;}=new();
    public string[] AllowedFolders{get;set;}=[];
}
