// Compatibility bridge only; original R2 host, not the 0.5.26/1.1.12 baseline.
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using WinUIGui;
if(!AppContext.BaseDirectory.Contains("gpt-review-r3")||!AppContext.BaseDirectory.Contains("host-audit"))throw new Exception("Wrong scope");
var hosts=new Dictionary<string,ExtensionHostWindow>();
string? line;
while((line=Console.ReadLine())!=null){try{var c=JsonNode.Parse(line)!;string op=c["op"]!.GetValue<string>(),name=c["name"]?.GetValue<string>()??"";object value;
if(op=="new"){var h=new ExtensionHostWindow("epub","R2 compatibility",AppContext.BaseDirectory,"index.html");h.AuditInitialize();h.AuditActivate(WindowActivationState.CodeActivated);hosts.Add(name,h);value=new{session=h.AuditSession,source=h.Wv.CoreWebView2.LastNavigation,backend="r2"};}
else if(op=="send"){var h=hosts[name];int n=h.Wv.CoreWebView2.Replies.Count;h.AuditRaw(c["raw"]!.GetValue<string>());value=new{replies=h.Wv.CoreWebView2.Replies.Skip(n).ToArray()};}
else throw new Exception("Unknown op");Console.WriteLine(JsonSerializer.Serialize(new{value}));
}catch(Exception e){Console.WriteLine(JsonSerializer.Serialize(new{bridgeError=e.ToString()}));}}
