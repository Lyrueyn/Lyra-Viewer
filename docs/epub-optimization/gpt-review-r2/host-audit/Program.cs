// Independent R2 acceptance: compile-links the frozen host/protocol/store verbatim.
// Headless event doubles only. All actual disk I/O is inside this audit output directory.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using WinUIGui;

internal static class Program {
 static readonly List<object> Results=new();
 static readonly string Base=AppContext.BaseDirectory;
 static readonly string Evidence=Path.Combine(Base,"evidence");
 static ReaderStateStore Store=null!;
 static string Book(char c)=>"sha256:"+new string(c,64);
 static string StatePath(string b)=>Path.Combine(Store.Root,"books",b[7..]+".json");
 static string PrefsPath=>Path.Combine(Store.Root,"preferences.json");
 static JsonElement Json(string s){using var d=JsonDocument.Parse(s);return d.RootElement.Clone();}
 static void Record(string id,bool pass,object data){Results.Add(new{id,pass,data});Console.WriteLine($"{(pass?"PASS":"FAIL")} {id}");}
 static void Case(string id,Action test){try{test();}catch(Exception ex){Record(id,false,new{probeError=ex.ToString()});}}
 static ExtensionHostWindow Host(){var h=new ExtensionHostWindow("epub","independent audit",Base,"index.html");h.AuditInitialize();return h;}
 static JsonObject Request(ExtensionHostWindow h,string type,JsonObject? fields=null,string? session=null)=>h.AuditRequest(type,fields,session)??throw new Exception("No reply for "+type);
 static bool Ok(JsonObject r)=>r["ok"]?.GetValue<bool>()==true;
 static string? Code(JsonObject r)=>r["error"]?["code"]?.GetValue<string>();
 static long Token(ExtensionHostWindow h,string b)=>Request(h,"state.interact",new(){["bookId"]=b})["result"]!["interactionToken"]!.GetValue<long>();
 static JsonObject Save(ExtensionHostWindow h,string b,string quote,long token,long sequence,string? session=null)=>Request(h,"state.savePosition",new(){["bookId"]=b,["interactionToken"]=token,["sequence"]=sequence,["position"]=new JsonObject{["spineHref"]="synthetic.xhtml",["locator"]=new JsonObject{["quote"]=quote}}},session);
 static string? Quote(string b)=>Store.Load(b,"observer")["position"]?["locator"]?["quote"]?.GetValue<string>();
 static void ArchivePrefs(string name){if(File.Exists(PrefsPath))File.Move(PrefsPath,Path.Combine(Evidence,name+"-preferences.json"));}
 static JsonObject Pref(ExtensionHostWindow h,long seq,JsonObject p)=>Request(h,"state.savePreferences",new(){["sequence"]=seq,["preferences"]=p});
 static void Send(ExtensionHostWindow h,string type,JsonObject? fields=null,string? session=null){h.AuditRequest(type,fields,session);}
 static void Main(string[] args){
  if(!Base.Contains("gpt-review-r2",StringComparison.OrdinalIgnoreCase)||!Base.Contains("host-audit",StringComparison.OrdinalIgnoreCase))throw new Exception("Refuse output outside independent audit");
  if(Directory.Exists(Path.Combine(Base,"data")))throw new Exception("Choose fresh output directory; evidence never overwritten");
  Directory.CreateDirectory(Evidence);Store=new ReaderStateStore();
  var a=Host();var b=Host();a.AuditActivate(WindowActivationState.CodeActivated);b.AuditActivate(WindowActivationState.CodeActivated);
  Case("H-ACTIVE-TOKEN",()=>{
   string book=Book('a');long ta=Token(a,book);Save(a,book,"A first",ta,10);a.AuditActivate(WindowActivationState.Deactivated);long tb=Token(b,book);Save(b,book,"B latest",tb,20);
   var inactive=Request(a,"state.interact",new(){["bookId"]=book});var old=Save(a,book,"A late",ta,30);var owner=Save(a,book,"A borrowed",tb,30);var future=Save(a,book,"A forged",tb+100,31);var rollback=Save(b,book,"B old sequence",tb,2);var duplicate=Save(b,book,"B different same seq",tb,20);var idem=Save(b,book,"B latest",tb,20);
   string? beforeTakeover=Quote(book);a.AuditActivate(WindowActivationState.PointerActivated);long take=Token(a,book);var takeover=Save(a,book,"A new interaction",take,40);
   Record("H-ACTIVE-TOKEN",Code(inactive)=="WINDOW_NOT_ACTIVE"&&Code(old)=="STALE_INTERACTION"&&Code(owner)=="TOKEN_OWNER_MISMATCH"&&Code(future)=="STALE_INTERACTION"&&Code(rollback)=="STALE_SEQUENCE"&&Code(duplicate)=="DUPLICATE_SEQUENCE"&&Ok(idem)&&beforeTakeover=="B latest"&&Ok(takeover),new{inactive,old,owner,future,rollback,duplicate,idem,beforeTakeover,takeover,final=Quote(book)});
  });
  Case("H-REACTIVATED-OLD-INTERACT",()=>{
   var oldWindow=Host();var newWindow=Host();string book=Book('5');oldWindow.AuditActivate(WindowActivationState.PointerActivated);
   // Request created during earlier reading action, intentionally delivered only after focus returns.
   var delayed=new JsonObject{["bookId"]=book};string oldSession=oldWindow.AuditSession;
   oldWindow.AuditActivate(WindowActivationState.Deactivated);newWindow.AuditActivate(WindowActivationState.PointerActivated);long newer=Token(newWindow,book);Save(newWindow,book,"B latest explicit operation",newer,100);
   oldWindow.AuditActivate(WindowActivationState.CodeActivated); // focus only; no fresh reading action
   var staleGrant=Request(oldWindow,"state.interact",delayed,oldSession);long granted=staleGrant["result"]?["interactionToken"]?.GetValue<long>()??0;var staleWrite=Save(oldWindow,book,"A earlier action delayed",granted,101);
   Record("H-REACTIVATED-OLD-INTERACT",Quote(book)=="B latest explicit operation",new{newer,staleGrant,staleWrite,final=Quote(book),boundary="Controlled late interact delivered after reactivation; no new reading action; not native latency measurement"});
  });
  Case("H-IDEMPOTENT-UNICODE",()=>{
   string book=Book('6');long t=Token(a,book);var first=Save(a,book,"相同位置",t,1);var retry=Save(a,book,"相同位置",t,1);Record("H-IDEMPOTENT-UNICODE",Ok(retry),new{first,retry,note="Identical compact raw JSON with unescaped Chinese on both requests"});
  });
  Case("H-READ-FAIL-WRITE-ALLOWED",()=>{
   string book=Book('b');long t=Token(a,book);Save(a,book,"preserve position",t,1);Request(a,"bookmark.add",new(){["bookId"]=book,["bookmark"]=new JsonObject{["id"]="preserved-bm"}});
   string path=StatePath(book);byte[] before=File.ReadAllBytes(path);JsonObject load,grant,save,add,delete;
   using(var held=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.Write|FileShare.Delete)){
    load=Request(a,"state.load",new(){["bookId"]=book});grant=Request(a,"state.interact",new(){["bookId"]=book});save=Save(a,book,"should not overwrite",t,2);add=Request(a,"bookmark.add",new(){["bookId"]=book,["bookmark"]=new JsonObject{["id"]="new-bm"}});delete=Request(a,"bookmark.delete",new(){["bookId"]=book,["bookmarkId"]="preserved-bm"});
   }
   bool same=before.SequenceEqual(File.ReadAllBytes(path));Record("H-READ-FAIL-WRITE-ALLOWED",new[]{load,grant,save,add,delete}.All(r=>Code(r)=="STATE_UNREADABLE")&&same,new{load,grant,save,add,delete,same,position=Quote(book),bookmarkCount=Store.Load(book,"obs")["bookmarkCount"]?.GetValue<int>()});
  });
  Case("H-READONLY-WRITE",()=>{
   string book=Book('c');long t=Token(a,book);Save(a,book,"old bytes",t,1);string p=StatePath(book);byte[] before=File.ReadAllBytes(p);JsonObject res;
   File.SetAttributes(p,FileAttributes.ReadOnly);try{res=Save(a,book,"new bytes",t,2);}finally{File.SetAttributes(p,FileAttributes.Normal);}
   bool same=before.SequenceEqual(File.ReadAllBytes(p));int tmp=Directory.GetFiles(Path.GetDirectoryName(p)!,Path.GetFileName(p)+".tmp-*").Length;
   Record("H-READONLY-WRITE",Code(res)=="WRITE_FAILED"&&same&&tmp==0,new{res,same,tmp});
  });
  Case("H-UNKNOWN-VERSION",()=>{
   string book=Book('d'),p=StatePath(book);File.WriteAllText(p,"{\"version\":999,\"position\":{\"important\":true},\"bookmarks\":[{\"id\":\"keep\"}]}");byte[] before=File.ReadAllBytes(p);
   var load=Request(a,"state.load",new(){["bookId"]=book});var grant=Request(a,"state.interact",new(){["bookId"]=book});bool same=before.SequenceEqual(File.ReadAllBytes(p));Record("H-UNKNOWN-VERSION",Code(load)=="STATE_VERSION_UNSUPPORTED"&&Code(grant)=="STATE_VERSION_UNSUPPORTED"&&same,new{load,grant,same});
  });
  Case("H-CORRUPT-BACKUP",()=>{
   string book=Book('e'),p=StatePath(book);string original="{not valid json";File.WriteAllText(p,original);var grant=Request(a,"state.interact",new(){["bookId"]=book});string[] backups=Directory.GetFiles(Path.GetDirectoryName(p)!,Path.GetFileName(p)+".corrupt-*");bool backed=backups.Any(x=>File.ReadAllText(x)==original);Record("H-CORRUPT-BACKUP",Ok(grant)&&backed,new{grant,backed,backupFiles=backups});
  });
  Case("H-BOOKMARK-FULL-ENVELOPE",()=>{
   string book=Book('f');string chinese=new('汉',1250);var addErrors=new List<string>();
   for(int i=0;i<51;i++){var r=Request(a,"bookmark.add",new(){["bookId"]=book,["bookmark"]=new JsonObject{["id"]="long-"+i,["summary"]="摘要",["locator"]=new JsonObject{["quote"]=chinese}}});if(!Ok(r))addErrors.Add(r.ToJsonString());}
   var ids=new List<string>();var pages=new List<object>();string? cursor=null;int guard=0,maxBytes=0;
   do{var r=Request(a,"bookmark.list",new(){["bookId"]=book,["cursor"]=cursor,["limit"]=50});if(!Ok(r))throw new Exception(r.ToJsonString());string raw=a.Wv.CoreWebView2.Replies[^1];int bytes=Encoding.UTF8.GetByteCount(raw);maxBytes=Math.Max(maxBytes,bytes);var result=r["result"]!;var items=result["bookmarks"]!.AsArray();ids.AddRange(items.Select(x=>x!["id"]!.GetValue<string>()));cursor=result["nextCursor"]?.GetValue<string>();pages.Add(new{bytes,count=items.Count,cursor});if(++guard>20)throw new Exception("cursor did not terminate");}while(cursor!=null);
   Record("H-BOOKMARK-FULL-ENVELOPE",addErrors.Count==0&&ids.Count==51&&ids.Distinct().Count()==51&&maxBytes<=ReaderProtocol.MaxMessageBytes,new{pages,count=ids.Count,unique=ids.Distinct().Count(),maxBytes,limit=ReaderProtocol.MaxMessageBytes,addErrors});
  });
  Case("H-BOOKMARK-COUNT-BOUNDARIES",()=>{
   string book="sha256:"+new string('1',64);var boundaries=new List<object>();bool correct=true;
   foreach(int target in new[]{0,1,50,51,499,500}){int current=Store.Load(book,"obs")["bookmarkCount"]!.GetValue<int>();for(int i=current;i<target;i++)Store.AddBookmark(book,a.AuditSession,Json("{\"id\":\"small-"+i+"\"}"));
    string? cursor=null;var seen=new List<string>();int pages=0;do{var r=Store.ListBookmarks(book,cursor!,50);seen.AddRange(r["bookmarks"]!.AsArray().Select(x=>x!["id"]!.GetValue<string>()));cursor=r["nextCursor"]?.GetValue<string>();if(++pages>20)throw new Exception("cursor did not terminate");}while(cursor!=null);correct&=seen.Count==target&&seen.Distinct().Count()==target;boundaries.Add(new{target,count=seen.Count,unique=seen.Distinct().Count(),pages});}
   byte[] before=File.ReadAllBytes(StatePath(book));var cap=Request(a,"bookmark.add",new(){["bookId"]=book,["bookmark"]=new JsonObject{["id"]="extra-501"}});bool same=before.SequenceEqual(File.ReadAllBytes(StatePath(book)));Record("H-BOOKMARK-COUNT-BOUNDARIES",correct&&Code(cap)=="BOOKMARK_LIMIT"&&same,new{boundaries,cap,same});
  });
  // Real JavaScript-generated wire envelopes replayed at the original message entry.
  var input=JsonNode.Parse(File.ReadAllText(args[0]))!["scenarios"]!.AsArray();
  foreach(var scenario in input){string id=scenario!["id"]!.GetValue<string>();Case(id,()=>{
   if(scenario["requests"] is JsonArray requests){ArchivePrefs("before-"+id);var hosts=new Dictionary<string,ExtensionHostWindow>();var replies=new List<JsonObject>();foreach(var reqNode in requests){var req=reqNode!.AsObject();string session=req["sessionId"]!.GetValue<string>();if(!hosts.TryGetValue(session,out var h)){h=Host();Request(h,"hello",null,session);hosts[session]=h;}h.AuditRaw(req.ToJsonString());replies.Add(JsonNode.Parse(h.Wv.CoreWebView2.Replies[^1])!.AsObject());}
    var prefs=Store.Load(Book('0'),"observer")["preferences"]!.DeepClone();int font=prefs["fontSize"]?.GetValue<int>()??0;Record(id,font==32,new{expectedFont=32,actualFont=font,requests=requests.DeepClone(),replies,prefs});ArchivePrefs(id);
   }else if(id=="H-BOOKMARK-LOCAL-PARITY"){
    var result=Request(a,"bookmark.add",new(){["bookId"]=Book('2'),["bookmark"]=scenario["bookmark"]!.DeepClone()});Record(id,Code(result)=="BOOKMARK_TOO_LARGE",new{result,note="Same bookmark accepted by local JS; host correctly refuses"});
   }else if(id=="H-CLOCK-ROLLBACK"){
    string book=Book('3');long token=Token(a,book),s1=scenario["sequences"]![0]!.GetValue<long>(),s2=scenario["sequences"]![1]!.GetValue<long>();var first=Save(a,book,"before clock correction",token,s1);var second=Save(a,book,"after clock correction",token,s2);Record(id,Ok(second),new{first,second,s1,s2,final=Quote(book)});
   }
  });}
  Case("H-PREF-SHARED-LOCK",()=>{
   ArchivePrefs("before-lock");bool all=true;for(int i=0;i<10;i++){long seq=1000+i;Parallel.Invoke(()=>new ReaderStateStore().SavePreferences("A",seq,Json("{\"fontSize\":28}")),()=>new ReaderStateStore().SavePreferences("B",seq,Json("{\"lineHeight\":2.2}")));var p=Store.Load(Book('0'),"observer")["preferences"]!;all&=p["fontSize"]?.GetValue<int>()==28&&p["lineHeight"]?.GetValue<double>()==2.2;}
   Record("H-PREF-SHARED-LOCK",all,new{all,rounds=10,boundary="API thread-safety check, not a claim about WinUI thread concurrency"});ArchivePrefs("shared-lock");
  });
  Case("H-PREF-FUTURE-SEQUENCE",()=>{
   var huge=Pref(a,long.MaxValue,new(){["fontSize"]=18});var fresh=Pref(b,1800000000000000,new(){["fontSize"]=32});var p=Store.Load(Book('0'),"observer")["preferences"]!.DeepClone();Record("H-PREF-FUTURE-SEQUENCE",p["fontSize"]!.GetValue<int>()==32,new{huge,fresh,p,note="Directed authority test; no claim that normal UI generates Int64.MaxValue"});ArchivePrefs("future-sequence");
  });
  Case("H-REFRESH-OLD-HELLO",()=>{
   var h=Host();h.AuditActivate(WindowActivationState.CodeActivated);string book=Book('4'),old=h.AuditSession;long t=Token(h,book);Save(h,book,"before refresh",t,1);bool canceled=h.Wv.CoreWebView2.AuditNavigationStarting(CoreWebView2NavigationKind.Reload);string fresh=h.AuditSession;
   var withoutHello=h.AuditRequest("state.savePosition",new(){["bookId"]=book,["interactionToken"]=t,["sequence"]=2,["position"]=new JsonObject{["marker"]="old direct"}},old);
   var hello=Request(h,"hello",null,old);var revived=Save(h,book,"old page after refresh",t,3,old);Record("H-REFRESH-OLD-HELLO",!Ok(hello)&&!Ok(revived),new{canceled,old,fresh,sessionAfterOldHello=h.AuditSession,withoutHello,hello,revived,final=Quote(book),boundary="Old hello is deliberately delivered after reload; tests validation, not natural probability"});
  });
  Case("H-NEWDOCUMENT-SESSION",()=>{
   var h=Host();string before=h.AuditSession;h.Wv.CoreWebView2.AuditNavigationStarting(CoreWebView2NavigationKind.NewDocument);h.Wv.CoreWebView2.AuditNavigation(true);Record("H-NEWDOCUMENT-SESSION",before!=h.AuditSession,new{before,after=h.AuditSession});
  });
  Case("H-LEGACY-OLD-NAV",()=>{
   var h=Host();string oldSource=h.AuditOrigin;h.Wv.CoreWebView2.AuditNavigationStarting(CoreWebView2NavigationKind.Reload);h.AuditRaw("\"lyra-book-ready\"",oldSource);Record("H-LEGACY-OLD-NAV",!h.AuditReady,new{ready=h.AuditReady,overlay=h.LoadingOverlay.Visibility.ToString(),oldSource,note="Same origin remains across navigations; legacy ready has no generation"});
  });
  Case("H-LIFECYCLE-CONTROLS",()=>{
   var h=Host();Send(h,"app.ready");Send(h,"book.begin",new(){["loadId"]="bad"});Send(h,"book.error",new(){["loadId"]="bad",["message"]="failed"});Send(h,"book.begin",new(){["loadId"]="good"});Send(h,"book.ready");bool missingRejected=!h.AuditReady;Send(h,"book.ready",new(){["loadId"]="old"});bool oldRejected=!h.AuditReady;Send(h,"book.ready",new(){["loadId"]="good"});bool hidden=h.LoadingOverlay.Visibility==Visibility.Collapsed;
   var bad=Host();bad.Wv.CoreWebView2.AuditNavigation(false);bad.AuditTickOwnTimers();var foreign=Host();foreign.AuditRaw("\"lyra-book-ready\"","https://other.invalid/");Record("H-LIFECYCLE-CONTROLS",missingRejected&&oldRejected&&hidden&&bad.LoadingOverlay.Visibility==Visibility.Visible&&!foreign.AuditReady,new{missingRejected,oldRejected,hidden,navErrorVisible=bad.LoadingOverlay.Visibility.ToString(),foreignReady=foreign.AuditReady});
  });
  Case("H-CLOSE-NO-ACK-TIMEOUT",()=>{
   var h=Host();h.AppWindow.AuditClosing();var posted=h.Wv.CoreWebView2.Replies.Select(x=>JsonNode.Parse(x)).ToArray();bool running=h.AuditCloseTimerRunning;h.AuditTickOwnTimers();Record("H-CLOSE-NO-ACK-TIMEOUT",running&&h.ClosedCount==1&&!h.AuditFinalizePending,new{posted,hadCloseTimer=running,h.ClosedCount,ackDelivered=false,note="Timeout and acknowledgement both call FinishFinalizeAndClose; FINALIZED alone is not ack proof"});
  });
  Case("H-CLOSE-NAV-FAIL-CANCELS-DEADLINE",()=>{
   var h=Host();h.AppWindow.AuditClosing();bool before=h.AuditCloseTimerRunning;h.Wv.CoreWebView2.AuditNavigation(false);h.AuditTickOwnTimers();h.AppWindow.AuditClosing();Record("H-CLOSE-NAV-FAIL-CANCELS-DEADLINE",h.ClosedCount>0,new{before,after=h.AuditCloseTimerRunning,h.AuditFinalizePending,h.ClosedCount,note="NavigationCompleted failure stops every timer, including close deadline; subsequent Closing remains canceled"});
  });
  Case("H-CLOSE-EMPTY-ACK",()=>{
   var h=Host();h.AppWindow.AuditClosing();h.AuditRaw("{\"type\":\"host.finalized\"}");Record("H-CLOSE-EMPTY-ACK",h.ClosedCount==0,new{h.ClosedCount,note="Empty requestId accepted without session validation"});
  });
  foreach(var t in DispatcherTimer.All)t.Stop();
  File.WriteAllText(Path.Combine(Base,"host-results.json"),JsonSerializer.Serialize(new{generatedAt=DateTime.UtcNow,baseDirectory=Base,evidenceKind="Original frozen C# linked verbatim; headless WinUI/WebView event doubles; actual isolated Windows files; not native desktop",results=Results},new JsonSerializerOptions{WriteIndented=true}));
  Console.WriteLine($"Recorded {Results.Count} assertions. FAIL denotes unmet product expectation or explicit probeError, inspect JSON. Diagnostic exit zero on completion.");
 }
}
