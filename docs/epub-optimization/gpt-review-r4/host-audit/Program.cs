// Independent R4 bridge. Links frozen product C# verbatim. All filesystem fixtures live below this executable.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using WinUIGui;
internal static class Program {
 static readonly string Base=AppContext.BaseDirectory;
 static readonly Dictionary<string,ExtensionHostWindow> Hosts=new();
 static readonly Dictionary<string,FileStream> Locks=new();
 static ReaderStateStore Store=null!;
 static string Prefs=>Path.Combine(Store.Root,"preferences.json");
 static string FileFor(JsonObject c)=>c["bookId"] is JsonValue b?Path.Combine(Store.Root,"books",b.GetValue<string>()[7..]+".json"):Prefs;
 static object Snapshot(ExtensionHostWindow h)=>new{session=h.AuditSession,source=h.Wv.CoreWebView2?.LastNavigation??"",activationEpoch=h.AuditActivation,navEpoch=h.AuditNav,h.ClosedCount,pending=h.AuditFinalizePending,closeRequest=h.AuditFinalizeRequest,closeTimer=h.AuditCloseTimerRunning,ready=h.AuditReady,ensureCalls=h.Wv.EnsureCalls};
 static object Bridge(JsonObject c){
  string op=c["op"]!.GetValue<string>(),name=c["name"]?.GetValue<string>()??"";
  if(op=="new") {var h=new ExtensionHostWindow("epub","R4 audit",Base,"index.html");Hosts.Add(name,h);if(c["holdEnsure"]?.GetValue<bool>()==true)h.Wv.HoldEnsure();if(c["initialize"]?.GetValue<bool>()!=false)h.AuditInitialize();h.AuditActivate(WindowActivationState.CodeActivated);return Snapshot(h);}
  if(op=="prefs"||op=="book"){string p=FileFor(c);return new{path=p,raw=File.Exists(p)?File.ReadAllText(p):null};}
  if(op=="archivePrefs"){string target=Path.Combine(Base,"evidence",c["label"]!.GetValue<string>()+"-preferences.json");if(File.Exists(Prefs))File.Move(Prefs,target);return new{path=target};}
  if(op=="fixtureWrite"){string p=FileFor(c);File.WriteAllText(p,c["raw"]!.GetValue<string>());return new{path=p,raw=File.ReadAllText(p)};}
  if(op=="readonly"){string p=FileFor(c);File.SetAttributes(p,c["enabled"]!.GetValue<bool>()?FileAttributes.ReadOnly:FileAttributes.Normal);return new{path=p};}
  if(op=="holdFile"){string p=FileFor(c);Locks[name]=new FileStream(p,FileMode.Open,FileAccess.ReadWrite,FileShare.Write|FileShare.Delete);return new{path=p};}
  if(op=="releaseFile"){Locks[name].Dispose();Locks.Remove(name);return new{released=name};}
  if(op=="grantCount"){var t=typeof(ReaderStateStore);var d=t.GetField("PreferenceGrants",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;return new{count=d.GetType().GetProperty("Count")!.GetValue(d),order=t.GetField("_globalPreferenceOrder",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)};}
  if(op=="ageGrants"){var d=typeof(ReaderStateStore).GetField("PreferenceGrants",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;foreach(var pair in (System.Collections.IEnumerable)d){var g=pair.GetType().GetProperty("Value")!.GetValue(pair)!;g.GetType().GetField("CreatedAtUtc")!.SetValue(g,DateTime.UtcNow.AddMinutes(-11));}return new{fixture="Explicit reflection age only, original prune methods unchanged"};}
  var host=Hosts[name];
  if(op=="send"){int n=host.Wv.CoreWebView2.Replies.Count;host.AuditRaw(c["raw"]!.GetValue<string>(),c["source"]?.GetValue<string>());return new{replies=host.Wv.CoreWebView2.Replies.Skip(n).ToArray(),state=Snapshot(host)};}
  if(op=="activate"){host.AuditActivate(c["active"]!.GetValue<bool>()?WindowActivationState.CodeActivated:WindowActivationState.Deactivated);return new{state=Snapshot(host),replies=host.Wv.CoreWebView2?.Replies.TakeLast(1).ToArray()};}
  if(op=="close"){int n=host.Wv.CoreWebView2?.Replies.Count??0;host.AppWindow.AuditClosing();return new{replies=host.Wv.CoreWebView2?.Replies.Skip(n).ToArray()??Array.Empty<string>(),state=Snapshot(host)};}
  if(op=="tick")host.AuditTickOwnTimers();
  else if(op=="reload")host.Wv.CoreWebView2.AuditNavigationStarting(CoreWebView2NavigationKind.Reload);
  else if(op=="initialize")host.AuditInitialize();
  else if(op=="releaseEnsure")host.Wv.ReleaseEnsure();
  else if(op=="navigate")host.AuditNavigate();
  else if(op=="cleanup")host.AuditCleanup();
  else if(op=="navfail")host.AuditNavFail();
  else if(op=="unavailable")host.AuditSetStoreUnavailable();
  else if(op!="snapshot")throw new Exception("unknown op "+op);
  return Snapshot(host);
 }
 static void Main(){
  if(!Base.Contains("gpt-review-r4",StringComparison.OrdinalIgnoreCase)||!Base.Contains("host-audit",StringComparison.OrdinalIgnoreCase))throw new Exception("Output outside audit scope");
  Directory.CreateDirectory(Path.Combine(Base,"evidence"));Store=new();
  string? line;while((line=Console.ReadLine())!=null){try{Console.WriteLine(JsonSerializer.Serialize(new{value=Bridge(JsonNode.Parse(line)!.AsObject())}));}catch(Exception e){Console.WriteLine(JsonSerializer.Serialize(new{bridgeError=e.ToString()}));}}
  foreach(var l in Locks.Values)l.Dispose();
 }
}
