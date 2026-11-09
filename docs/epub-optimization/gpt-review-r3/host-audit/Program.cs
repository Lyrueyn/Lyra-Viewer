// R3 independent audit: original frozen C# is Compile Link'ed, no product edits.
// --bridge exchanges exact JSON strings with the original JS under a headless transport double.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using WinUIGui;
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class Program {
 static readonly string Base=AppContext.BaseDirectory;
 static readonly string Evidence=Path.Combine(Base,"evidence");
 static readonly List<object> Results=new();
 static readonly Dictionary<string,ExtensionHostWindow> Hosts=new();
 static ReaderStateStore Store=null!;
 static string Book(char c)=>"sha256:"+new string(c,64);
 static string StatePath(string b)=>Path.Combine(Store.Root,"books",b[7..]+".json");
 static string PrefsPath=>Path.Combine(Store.Root,"preferences.json");
 static JsonElement Json(string s){using var d=JsonDocument.Parse(s);return d.RootElement.Clone();}
 static bool Ok(JsonObject? r)=>r?["ok"]?.GetValue<bool>()==true;
 static string? Code(JsonObject? r)=>r?["error"]?["code"]?.GetValue<string>();
 static ExtensionHostWindow Host(){var h=new ExtensionHostWindow("epub","independent audit",Base,"index.html");h.AuditInitialize();h.AuditActivate(WindowActivationState.CodeActivated);return h;}
 static JsonObject Req(ExtensionHostWindow h,string t,JsonObject? f=null)=>h.AuditRequest(t,f)??throw new Exception("no reply "+t);
 static JsonObject Grant(ExtensionHostWindow h,string b)=>Req(h,"state.interact",h.AuditEpoch(b));
 static long Token(ExtensionHostWindow h,string b)=>Grant(h,b)["result"]!["interactionToken"]!.GetValue<long>();
 static JsonObject Save(ExtensionHostWindow h,string b,long token,long seq,string marker)=>Req(h,"state.savePosition",new(){["bookId"]=b,["interactionToken"]=token,["sequence"]=seq,["position"]=new JsonObject{["marker"]=marker}});
 static JsonObject Pref(ExtensionHostWindow h,string b,long version,JsonObject patch)=>Req(h,"state.savePreferences",new(){["bookId"]=b,["preferences"]=patch,["fieldVersions"]=new JsonObject(patch.Select(x=>new KeyValuePair<string,JsonNode?>(x.Key,JsonValue.Create(version))))});
 static string? Marker(string b)=>Store.Load(b,"observer")["position"]?["marker"]?.GetValue<string>();
 static void Record(string id,bool pass,object data){Results.Add(new{id,pass,data});Console.WriteLine($"{(pass?"PASS":"FAIL")} {id}");}
 static void Case(string id,Action action){try{action();}catch(Exception e){Record(id,false,new{probeError=e.ToString()});}}
 static string? ArchivePrefs(string name){if(!File.Exists(PrefsPath))return null;string dest=Path.Combine(Evidence,name+"-preferences.json");File.Move(PrefsPath,dest);return dest;}
 static object Snapshot(ExtensionHostWindow h)=>new{session=h.AuditSession,source=h.AuditOrigin,activationEpoch=h.AuditActivation,navEpoch=h.AuditNav,h.ClosedCount,pending=h.AuditFinalizePending,closeRequest=h.AuditFinalizeRequest,closeTimer=h.AuditCloseTimerRunning,ready=h.AuditReady};
 static object Bridge(JsonObject cmd){
  string op=cmd["op"]!.GetValue<string>(); string name=cmd["name"]?.GetValue<string>()??"";
  if(op=="new"){var h=Host();Hosts.Add(name,h);return Snapshot(h);}
  if(op=="seedSafeCounter"){
   // Diagnostic control only: isolates ordering from the independent >2^53 product defect.
   string? archived=ArchivePrefs("bridge-"+cmd["label"]!.GetValue<string>());
   typeof(ReaderStateStore).GetField("_globalFieldSequence",BindingFlags.NonPublic|BindingFlags.Static)!.SetValue(null,100000L);
   return new{counter=100000,archived,fixture="Private counter seeded to safe integer; original grant/save methods unchanged"};
  }
  if(op=="prefs")return new{raw=File.Exists(PrefsPath)?File.ReadAllText(PrefsPath):null};
  if(op=="book") {string p=StatePath(cmd["bookId"]!.GetValue<string>());return new{raw=File.Exists(p)?File.ReadAllText(p):null};}
  if(op=="archivePrefs")return new{path=ArchivePrefs(cmd["label"]!.GetValue<string>())};
  var host=Hosts[name];
  if(op=="send"){int before=host.Wv.CoreWebView2.Replies.Count;host.AuditRaw(cmd["raw"]!.GetValue<string>(),cmd["source"]?.GetValue<string>());return new{replies=host.Wv.CoreWebView2.Replies.Skip(before).ToArray(),state=Snapshot(host)};}
  if(op=="activate"){host.AuditActivate(cmd["active"]!.GetValue<bool>()?WindowActivationState.CodeActivated:WindowActivationState.Deactivated);return Snapshot(host);}
  if(op=="close"){int before=host.Wv.CoreWebView2.Replies.Count;host.AppWindow.AuditClosing();return new{replies=host.Wv.CoreWebView2.Replies.Skip(before).ToArray(),state=Snapshot(host)};}
  if(op=="tick"){host.AuditTickOwnTimers();return Snapshot(host);}
  if(op=="snapshot")return Snapshot(host);
  if(op=="reload"){host.Wv.CoreWebView2.AuditNavigationStarting(CoreWebView2NavigationKind.Reload);return Snapshot(host);}
  if(op=="unavailable"){host.AuditSetStoreUnavailable();return Snapshot(host);}
  throw new Exception("unknown op "+op);
 }
 static void Main(string[] args){
  if(!Base.Contains("gpt-review-r3",StringComparison.OrdinalIgnoreCase)||!Base.Contains("host-audit",StringComparison.OrdinalIgnoreCase))throw new Exception("Output outside independent scope");
  Directory.CreateDirectory(Evidence);
  if(args.Contains("--bridge")){Store=new();string? line;while((line=Console.ReadLine())!=null){try{var c=JsonNode.Parse(line)!.AsObject();Console.WriteLine(JsonSerializer.Serialize(new{value=Bridge(c)}));}catch(Exception e){Console.WriteLine(JsonSerializer.Serialize(new{bridgeError=e.ToString()}));}}return;}
  if(Directory.Exists(Path.Combine(Base,"data")))throw new Exception("Fresh output required; refusing to overwrite evidence");
  Store=new();var a=Host();var b=Host();
  Case("H01-ACTIVE-TOKEN-SEQUENCE",()=>{
   string book=Book('a');long ta=Token(a,book);Save(a,book,ta,10,"A first");a.AuditActivate(WindowActivationState.Deactivated);long tb=Token(b,book);Save(b,book,tb,20,"B latest");
   var inactive=Grant(a,book);var stale=Save(a,book,ta,21,"late");var owner=Save(a,book,tb,21,"borrowed");var future=Save(b,book,tb+100,21,"future");var reverse=Save(b,book,tb,19,"reverse");var duplicate=Save(b,book,tb,20,"different");var idem=Save(b,book,tb,20,"B latest");
   a.AuditActivate(WindowActivationState.CodeActivated);long fresh=Token(a,book);var low=Save(a,book,fresh,1,"A new");
   Record("H01-ACTIVE-TOKEN-SEQUENCE",Code(inactive)=="WINDOW_NOT_ACTIVE"&&Code(stale)=="STALE_INTERACTION"&&Code(owner)=="TOKEN_OWNER_MISMATCH"&&Code(future)=="STALE_INTERACTION"&&Code(reverse)=="STALE_SEQUENCE"&&Code(duplicate)=="DUPLICATE_SEQUENCE"&&Ok(idem)&&Ok(low),new{inactive,stale,owner,future,reverse,duplicate,idem,low});
  });
  Case("H02-EPOCH-REACTIVATION",()=>{
   var h=Host();string book=Book('b');var delayed=h.AuditEpoch(book);h.AuditActivate(WindowActivationState.Deactivated);long tb=Token(b,book);Save(b,book,tb,1,"B latest");h.AuditActivate(WindowActivationState.CodeActivated);var old=Req(h,"state.interact",delayed);var fresh=Grant(h,book);
   Record("H02-EPOCH-REACTIVATION",Code(old)=="STALE_OPERATION_EPOCH"&&Ok(fresh),new{old,fresh,boundary="Controlled delayed original event; not native frequency"});
  });
  Case("H03-NAV-HELLO-LEGACY",()=>{
   var rows=new List<object>();bool pass=true;
   foreach(var kind in new[]{CoreWebView2NavigationKind.Reload,CoreWebView2NavigationKind.NewDocument}){
    var h=Host();string old=h.AuditSession,source=h.AuditOrigin;h.Wv.CoreWebView2.AuditNavigationStarting(kind);string fresh=h.AuditSession;
    var hello=h.AuditRequest("hello",null,old,source);h.AuditRaw("\"lyra-book-ready\"",source);bool oldReady=h.AuditReady;
    var save=h.AuditRequest("state.savePosition",new(){["bookId"]=Book('c'),["position"]=new JsonObject(),["sequence"]=1,["interactionToken"]=1},old,source);
    h.AuditRequest("book.error",new(){["loadId"]=h.AuditLoad,["message"]="old error"},old,source);
    var now=h.AuditRequest("hello");pass&=old!=fresh&&hello==null&&save==null&&!oldReady&&Ok(now);rows.Add(new{kind=kind.ToString(),old,fresh,hello,save,oldReady,now});
   }Record("H03-NAV-HELLO-LEGACY",pass,rows);
  });
  Case("H04-LEGACY-0526-PDF-EVENTS",()=>{
   var epub=Host();epub.AuditRaw("\"lyra-book-ready\"");var pdf=Host();pdf.AuditRaw("{\"type\":\"lyra-book-ready\"}");var foreign=Host();foreign.AuditRaw("\"lyra-book-ready\"","https://foreign/");
   epub.AppWindow.AuditClosing();epub.AuditTickOwnTimers();pdf.AppWindow.AuditClosing();pdf.AuditTickOwnTimers();
   Record("H04-LEGACY-0526-PDF-EVENTS",epub.AuditReady&&pdf.AuditReady&&!foreign.AuditReady&&epub.ClosedCount==1&&pdf.ClosedCount==1,new{epub=Snapshot(epub),pdf=Snapshot(pdf),foreign=Snapshot(foreign),boundary="Only legacy ready object/string and no-ack closing; not actual old EPUB/PDF application"});
  });
  Case("H05-CLOSE-CORRELATION",()=>{
   var h=Host();h.AppWindow.AuditClosing();h.AuditRaw("{\"type\":\"host.finalized\"}");h.AuditRaw(new JsonObject{["type"]="host.finalized",["requestId"]="wrong",["ok"]=true}.ToJsonString());bool noWrong=h.ClosedCount==0;
   h.AuditRaw(new JsonObject{["type"]="host.finalized",["requestId"]=h.AuditFinalizeRequest,["sessionId"]="wrong",["ok"]=true}.ToJsonString());bool noSession=h.ClosedCount==0;
   h.AuditRaw(new JsonObject{["type"]="host.finalized",["requestId"]=h.AuditFinalizeRequest,["sessionId"]=h.AuditSession,["ok"]=true,["revision"]=9}.ToJsonString());
   Record("H05-CLOSE-CORRELATION",noWrong&&noSession&&h.ClosedCount==1,new{noWrong,noSession,final=Snapshot(h)});
  });
  Case("H06-CLOSE-MISSING-SESSION",()=>{var h=Host();h.AppWindow.AuditClosing();h.AuditRaw(new JsonObject{["type"]="host.finalized",["requestId"]=h.AuditFinalizeRequest,["ok"]=true}.ToJsonString());Record("H06-CLOSE-MISSING-SESSION",h.ClosedCount==0,new{expected="reject missing session",actual=Snapshot(h)});});
  Case("H07-CLOSE-DEADLINE-NAVFAIL",()=>{var h=Host();h.AppWindow.AuditClosing();h.Wv.CoreWebView2.AuditNavigation(false);bool timer=h.AuditCloseTimerRunning;h.AuditTickOwnTimers();Record("H07-CLOSE-DEADLINE-NAVFAIL",timer&&h.ClosedCount==1,new{timer,final=Snapshot(h)});});
  Case("H08-IDEMPOTENT-UNICODE",()=>{string book=Book('d');long t=Token(a,book);var first=Req(a,"state.savePosition",new(){["bookId"]=book,["interactionToken"]=t,["sequence"]=1,["position"]=JsonNode.Parse("{\"quote\":\"中文😀\",\"page\":1}")});var second=Req(a,"state.savePosition",new(){["bookId"]=book,["interactionToken"]=t,["sequence"]=1,["position"]=JsonNode.Parse("{\"page\":1,\"quote\":\"\\u4e2d\\u6587😀\"}")});Record("H08-IDEMPOTENT-UNICODE",Ok(first)&&Ok(second)&&second["result"]?["idempotent"]?.GetValue<bool>()==true,new{first,second});});
  Case("H09-READFAIL-WRITABLE",()=>{string book=Book('e');long t=Token(a,book);Save(a,book,t,1,"keep");Req(a,"bookmark.add",new(){["bookId"]=book,["bookmark"]=new JsonObject{["id"]="keep"}});byte[] before=File.ReadAllBytes(StatePath(book));var replies=new List<JsonObject>();using(var held=new FileStream(StatePath(book),FileMode.Open,FileAccess.ReadWrite,FileShare.Write|FileShare.Delete)){replies.Add(Req(a,"state.load",new(){["bookId"]=book}));replies.Add(Grant(a,book));replies.Add(Save(a,book,t,2,"lost"));replies.Add(Req(a,"bookmark.add",new(){["bookId"]=book,["bookmark"]=new JsonObject{["id"]="new"}}));replies.Add(Req(a,"bookmark.delete",new(){["bookId"]=book,["bookmarkId"]="keep"}));}bool same=before.SequenceEqual(File.ReadAllBytes(StatePath(book)));Record("H09-READFAIL-WRITABLE",same&&replies.All(r=>Code(r)=="STATE_UNREADABLE"),new{same,replies});});
  Case("H10-WRITEFAIL-UNKNOWN-CORRUPT",()=>{string book=Book('f'),p=StatePath(book);long t=Token(a,book);Save(a,book,t,1,"keep");byte[] before=File.ReadAllBytes(p);JsonObject write;File.SetAttributes(p,FileAttributes.ReadOnly);try{write=Save(a,book,t,2,"new");}finally{File.SetAttributes(p,FileAttributes.Normal);}bool same=before.SequenceEqual(File.ReadAllBytes(p));int tmp=Directory.GetFiles(Path.GetDirectoryName(p)!,"*.tmp-*").Length;string unknown=Book('1');File.WriteAllText(StatePath(unknown),"{\"version\":999,\"position\":1}");var load=Req(a,"state.load",new(){["bookId"]=unknown});string broken=Book('2');File.WriteAllText(StatePath(broken),"{broken");var recover=Grant(a,broken);bool backup=Directory.GetFiles(Path.GetDirectoryName(p)!,new string('2',64)+".json.corrupt-*").Any(x=>File.ReadAllText(x)=="{broken");Record("H10-WRITEFAIL-UNKNOWN-CORRUPT",same&&tmp==0&&Code(write)=="WRITE_FAILED"&&Code(load)=="STATE_VERSION_UNSUPPORTED"&&Ok(recover)&&backup,new{same,tmp,write,load,recover,backup});});
  Case("H11-PREFS-FAILURE-MATRIX",()=>{ArchivePrefs("before-prefmatrix");string book=Book('3');long v=Grant(a,book)["result"]!["fieldVersion"]!.GetValue<long>();Pref(a,book,v,new(){["fontSize"]=32});byte[] before=File.ReadAllBytes(PrefsPath);JsonObject unreadable,write;long v2=Grant(a,book)["result"]!["fieldVersion"]!.GetValue<long>();using(var held=new FileStream(PrefsPath,FileMode.Open,FileAccess.ReadWrite,FileShare.Write|FileShare.Delete)){unreadable=Pref(a,book,v2,new(){["lineHeight"]=2.2});}bool sameRead=before.SequenceEqual(File.ReadAllBytes(PrefsPath));File.SetAttributes(PrefsPath,FileAttributes.ReadOnly);try{write=Pref(a,book,v2,new(){["lineHeight"]=2.2});}finally{File.SetAttributes(PrefsPath,FileAttributes.Normal);}bool sameWrite=before.SequenceEqual(File.ReadAllBytes(PrefsPath));Record("H11-PREFS-FAILURE-MATRIX",sameRead&&sameWrite&&Code(unreadable)=="PREFS_UNREADABLE"&&Code(write)=="WRITE_FAILED",new{unreadable,write,sameRead,sameWrite});ArchivePrefs("prefmatrix");});
  Case("H12-BOOKMARK-CAPACITY-BYTES",()=>{string book=Book('4');var rows=new List<object>();bool pass=true;foreach(int count in new[]{0,1,50,51,499,500}){int current=Store.Load(book,"obs")["bookmarkCount"]!.GetValue<int>();for(int i=current;i<count;i++)Store.AddBookmark(book,a.AuditSession,Json("{\"id\":\"bm"+i+"\"}"));string? cur=null;var ids=new List<string>();int pages=0;do{var page=Store.ListBookmarks(book,cur!,50);ids.AddRange(page["bookmarks"]!.AsArray().Select(x=>x!["id"]!.GetValue<string>()));cur=page["nextCursor"]?.GetValue<string>();if(++pages>20)throw new Exception("cursor loop");}while(cur!=null);pass&=ids.Count==count&&ids.Distinct().Count()==count;rows.Add(new{count,total=ids.Count,pages});}byte[] old=File.ReadAllBytes(StatePath(book));var cap=Req(a,"bookmark.add",new(){["bookId"]=book,["bookmark"]=new JsonObject{["id"]="extra"}});pass&=Code(cap)=="BOOKMARK_LIMIT"&&old.SequenceEqual(File.ReadAllBytes(StatePath(book)));string largeBook=Book('5');for(int i=0;i<51;i++)Req(a,"bookmark.add",new(){["bookId"]=largeBook,["bookmark"]=new JsonObject{["id"]="large"+i,["summary"]="摘要",["quote"]=new string('汉',1250)}});string? cursor=null;var sizes=new List<int>();var seen=new List<string>();do{var r=Req(a,"bookmark.list",new(){["bookId"]=largeBook,["cursor"]=cursor,["limit"]=50});sizes.Add(Encoding.UTF8.GetByteCount(a.Wv.CoreWebView2.Replies[^1]));seen.AddRange(r["result"]!["bookmarks"]!.AsArray().Select(x=>x!["id"]!.GetValue<string>()));cursor=r["result"]!["nextCursor"]?.GetValue<string>();}while(cursor!=null);Record("H12-BOOKMARK-CAPACITY-BYTES",pass&&seen.Count==51&&seen.Distinct().Count()==51&&sizes.All(s=>s<=ReaderProtocol.MaxMessageBytes),new{rows,cap,sizes,largeCount=seen.Count});});
  Case("H13-GRANT-AUTHORITY-FIELDS",()=>{ArchivePrefs("before-field");string book=Book('6');long va=Grant(a,book)["result"]!["fieldVersion"]!.GetValue<long>();var one=Pref(a,book,va,new(){["fontSize"]=18});long vb=Grant(b,book)["result"]!["fieldVersion"]!.GetValue<long>();var newer=Pref(b,book,vb,new(){["fontSize"]=32});var stale=Pref(a,book,va,new(){["fontSize"]=18});var huge=Pref(a,book,long.MaxValue,new(){["fontSize"]=16});var reuseForOtherField=Pref(a,book,va,new(){["lineHeight"]=2.2});Record("H13-GRANT-STALE-FUTURE",Ok(one)&&Ok(newer)&&Code(stale)=="PREFERENCE_STALE"&&Code(huge)=="PREFERENCE_STALE",new{one,newer,stale,huge});Record("H13-GRANT-FIELD-BINDING",!Ok(reuseForOtherField),new{expected="field grant binds to operation/field",reuseForOtherField,note="Directed protocol grant reuse; no claim of attack or ordinary UI fabrication"});ArchivePrefs("field-binding");});
  Case("H14-CONCURRENT-PREF-PATCH",()=>{string ba=Book('7'),bb=Book('8');long va=Grant(a,ba)["result"]!["fieldVersion"]!.GetValue<long>(),vb=Grant(b,bb)["result"]!["fieldVersion"]!.GetValue<long>();Parallel.Invoke(()=>new ReaderStateStore().SavePreferences(a.AuditSession,ba,Json("{\"fontSize\":28}"),new(){["fontSize"]=va}),()=>new ReaderStateStore().SavePreferences(b.AuditSession,bb,Json("{\"lineHeight\":2.2}"),new(){["lineHeight"]=vb}));var p=Store.Load(ba,"obs")["preferences"]!;Record("H14-CONCURRENT-PREF-PATCH",p["fontSize"]?.GetValue<int>()==28&&p["lineHeight"]?.GetValue<double>()==2.2,new{preferences=p.DeepClone(),boundary="Two Store instances/API threads, not native double-window scheduling"});ArchivePrefs("concurrent");});
  Case("H15-PREF-GRANT-INVALIDATES-POSITION",()=>{string book=Book('9');long token=Token(a,book);Save(a,book,token,1,"position before preference");var pf=hypref(a,book);var save=Save(a,book,token,2,"position after preference");Record("H15-PREF-GRANT-INVALIDATES-POSITION",Ok(save),new{token,prefGrant=pf,save,final=Marker(book)});});
  Case("H16-CORRUPT-BACKUP-DENIED",()=>{string book="sha256:"+new string('a',63)+"0",p=StatePath(book);File.WriteAllText(p,"{protected broken");var directory=new DirectoryInfo(Path.GetDirectoryName(p)!);var acl=System.IO.FileSystemAclExtensions.GetAccessControl(directory);var original=acl.GetSecurityDescriptorBinaryForm();var sid=System.Security.Principal.WindowsIdentity.GetCurrent().User!;acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,System.Security.AccessControl.FileSystemRights.CreateFiles,System.Security.AccessControl.InheritanceFlags.None,System.Security.AccessControl.PropagationFlags.None,System.Security.AccessControl.AccessControlType.Deny));JsonObject? load=null,grant=null;try{System.IO.FileSystemAclExtensions.SetAccessControl(directory,acl);load=Req(a,"state.load",new(){["bookId"]=book});grant=Grant(a,book);}finally{var restore=new System.Security.AccessControl.DirectorySecurity();restore.SetSecurityDescriptorBinaryForm(original);System.IO.FileSystemAclExtensions.SetAccessControl(directory,restore);}bool same=File.ReadAllText(p)=="{protected broken";Record("H16-CORRUPT-BACKUP-DENIED",same&&Code(load)=="STATE_UNREADABLE"&&Code(grant)=="STATE_UNREADABLE",new{load,grant,same,boundary="Temporary create-file deny ACL on this run's books directory only, restored in finally"});});
  Case("H17-BOOKMARK-SINGLE-PARITY",()=>{string book="sha256:"+new string('a',63)+"1";var rows=new List<object>();bool pass=true;foreach(var item in new[]{(id:"space",bm:new JsonObject{["id"]="  "},expected:"BAD_BOOKMARK"),(id:"summary256",bm:new JsonObject{["id"]="s256",["summary"]=new string('汉',256)},expected:""),(id:"summary257",bm:new JsonObject{["id"]="s257",["summary"]=new string('汉',257)},expected:"BOOKMARK_TOO_LARGE"),(id:"bytes4096",bm:new JsonObject{["id"]="byte4096",["payload"]=new string('x',4066)},expected:""),(id:"bytes4097",bm:new JsonObject{["id"]="byte4097",["payload"]=new string('x',4067)},expected:"BOOKMARK_TOO_LARGE")}){var r=Req(a,"bookmark.add",new(){["bookId"]=book,["bookmark"]=item.bm});pass&=(Code(r)??"")==item.expected;rows.Add(new{item.id,response=r});}Record("H17-BOOKMARK-SINGLE-PARITY",pass,rows);});
  foreach(var t in DispatcherTimer.All)t.Stop();
  File.WriteAllText(Path.Combine(Base,"host-results.json"),JsonSerializer.Serialize(new{generatedAt=DateTime.UtcNow,evidenceKind="Frozen original C# Compile Link; headless events; actual isolated Windows files; not native WinUI/WebView2",results=Results},new JsonSerializerOptions{WriteIndented=true}));
  Console.WriteLine($"Recorded {Results.Count} assertions. Product FAIL is in JSON; zero diagnostic exit means completed probes.");
 }
 static JsonObject hypref(ExtensionHostWindow h,string book){var fields=h.AuditEpoch(book);fields["purpose"]="preferences";return Req(h,"state.interact",fields);}
}
