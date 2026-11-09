using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Microsoft.UI.Xaml;
using WinUIGui;

internal static class Program {
 static readonly List<object> Results=new();
 static string Book(char c)=>"sha256:"+new string(c,64);
 static string StatePath(string book)=>Path.Combine(AppContext.BaseDirectory,"data","reader","epub","books",book[7..]+".json");
 static JsonElement Json(string raw){using var d=JsonDocument.Parse(raw);return d.RootElement.Clone();}
 static ExtensionHostWindow Host(){var h=new ExtensionHostWindow("epub","audit",AppContext.BaseDirectory,"index.html");h.AuditInitialize();return h;}
 static void Record(string id,bool defect,object details){var row=new{id,defectObserved=defect,details};Results.Add(row);Console.WriteLine(JsonSerializer.Serialize(row));}
 static bool Ok(JsonObject? r)=>r?["ok"]?.GetValue<bool>()==true;
 static long Token(ExtensionHostWindow h,string book)=>h.AuditRequest("state.interact",new(){["bookId"]=book})!["result"]!["interactionToken"]!.GetValue<long>();
 static JsonObject? Save(ExtensionHostWindow h,string book,string quote,long token,long seq)=>h.AuditRequest("state.savePosition",new(){["bookId"]=book,["interactionToken"]=token,["sequence"]=seq,["position"]=new JsonObject{["spineHref"]="synthetic.xhtml",["locator"]=new JsonObject{["quote"]=quote}}});
 static string? Quote(ReaderStateStore s,string book)=>s.Load(book,"observer")["position"]?["locator"]?["quote"]?.GetValue<string>();
 static void Main(){
  if(!AppContext.BaseDirectory.Contains("gpt-review-r1",StringComparison.OrdinalIgnoreCase)||!AppContext.BaseDirectory.Contains("host-audit",StringComparison.OrdinalIgnoreCase))throw new Exception("Refuse non-isolated data root");
  Environment.SetEnvironmentVariable("LYRA_READER_DEBUG",null);
  // Each run uses a newly built audit output directory. Never delete existing data.
  if(Directory.Exists(Path.Combine(AppContext.BaseDirectory,"data")))throw new Exception("Choose a fresh output directory: diagnostic refuses to overwrite previous results");
  var s=new ReaderStateStore();
  var a=Host();var b=Host();
  a.AuditActivate(WindowActivationState.CodeActivated);
  var ba=Book('a');long ta=Token(a,ba);Save(a,ba,"A first",ta,1);
  a.AuditActivate(WindowActivationState.Deactivated);b.AuditActivate(WindowActivationState.CodeActivated);
  long tb=Token(b,ba);Save(b,ba,"B latest legitimate operation",tb,1);
  var stale=Save(a,ba,"A delayed old token",ta,2);
  long inactiveToken=Token(a,ba);var overwrite=Save(a,ba,"A inactive overwrite",inactiveToken,3);
  Record("H-ACTIVE",a.AuditActive&&Ok(overwrite)&&Quote(s,ba)=="A inactive overwrite",new{activeAfterDeactivated=a.AuditActive,ta,tb,inactiveToken,stale,overwrite,finalQuote=Quote(s,ba)});

  var bb=Book('b');long ownerToken=Token(b,bb);
  var notOwner=Save(a,bb,"wrong session owner",ownerToken,1);
  Save(b,bb,"sequence 20",ownerToken,20);var replay=Save(b,bb,"sequence 2 arrives late",ownerToken,2);
  var future=Save(a,bb,"ungranted future token",ownerToken+100,3);
  Record("H-TOKEN",Ok(notOwner)&&Ok(replay)&&Ok(future),new{notOwner,replay,future,finalQuote=Quote(s,bb)});

  var bc=Book('c');var p1=a.AuditRequest("state.savePreferences",new(){["preferences"]=new JsonObject{["fontSize"]=28}});
  var p2=b.AuditRequest("state.savePreferences",new(){["preferences"]=new JsonObject{["theme"]="sepia"}});
  var merged=s.Load(bc,"observer")["preferences"]!.DeepClone();
  var newer=b.AuditRequest("state.savePreferences",new(){["preferences"]=new JsonObject{["fontSize"]=32}});
  var delayed=a.AuditRequest("state.savePreferences",new(){["preferences"]=new JsonObject{["fontSize"]=18}});
  Record("H-PREF-ORDER",s.Load(bc,"observer")["preferences"]!["fontSize"]!.GetValue<int>()==18,new{independentFieldMerge=merged,newer,delayed,final=s.Load(bc,"observer")["preferences"]});

  var bd=Book('d');int accepted=0;for(int i=0;i<50;i++){
   var bm=new JsonObject{["id"]="wide-"+i,["summary"]="合成",["spineHref"]="s.xhtml",["locator"]=new JsonObject{["quote"]=new string('汉',1250)}};
   var r=a.AuditRequest("bookmark.add",new(){["bookId"]=bd,["bookmark"]=bm});if(Ok(r))accepted++;else throw new Exception("Unexpected wide bookmark rejection "+r);
  }
  var page=a.AuditRequest("bookmark.list",new(){["bookId"]=bd,["limit"]=50})!;
  int bytes=Encoding.UTF8.GetByteCount(a.Wv.CoreWebView2.Replies[^1]);
  Record("H-PAGE-BYTES",accepted==50&&bytes>ReaderProtocol.MaxMessageBytes,new{accepted,responseBytes=bytes,max=ReaderProtocol.MaxMessageBytes,total=page["result"]!["total"],count=page["result"]!["bookmarks"]!.AsArray().Count});
  var be=Book('e');for(int i=0;i<500;i++)s.AddBookmark(be,"audit",Json("{\"id\":\"small-"+i+"\",\"summary\":\"synthetic\"}"));
  var cap=a.AuditRequest("bookmark.add",new(){["bookId"]=be,["bookmark"]=new JsonObject{["id"]="small-500"}});
  var tooLong=a.AuditRequest("bookmark.add",new(){["bookId"]=bd,["bookmark"]=new JsonObject{["id"]="summary-too-long",["summary"]=new string('a',257)}});
  int pageCount=0,readCount=0;string cursor="";do{var p=s.ListBookmarks(be,cursor,50);pageCount++;readCount+=p["bookmarks"]!.AsArray().Count;cursor=p["nextCursor"]?.GetValue<string>()??"";}while(cursor!="");
  Record("H-CAP-CONTROL",false,new{rejection501=cap,summary257=tooLong,pageCount,readCount,reopenCount=new ReaderStateStore().Load(be,"new-session")["bookmarkCount"]});

  var bf=Book('f');long tf=Token(a,bf);Save(a,bf,"saved before failure",tf,1);s.AddBookmark(bf,"audit",Json("{\"id\":\"retained\",\"summary\":\"synthetic\"}"));
  string fp=StatePath(bf);string before=File.ReadAllText(fp);File.SetAttributes(fp,FileAttributes.ReadOnly);
  JsonObject? failed;try{failed=Save(a,bf,"write should fail",tf,2);}finally{File.SetAttributes(fp,FileAttributes.Normal);}
  Record("H-WRITE-FAIL-CONTROL",false,new{response=failed,oldBytesPreserved=before==File.ReadAllText(fp),tmpFiles=Directory.GetFiles(Path.GetDirectoryName(fp)!,Path.GetFileName(fp)+".tmp-*").Length});
  JsonObject? grantAfterUnreadable;using(var deniedRead=new FileStream(fp,FileMode.Open,FileAccess.Read,FileShare.Write|FileShare.Delete)){
   grantAfterUnreadable=a.AuditRequest("state.interact",new(){["bookId"]=bf});
  }
  var afterUnreadable=s.Load(bf,"observer");
  Record("H-READ-FAIL-DATA-LOSS",Ok(grantAfterUnreadable)&&afterUnreadable["position"]==null&&afterUnreadable["bookmarkCount"]!.GetValue<int>()==0,new{grantAfterUnreadable,afterUnreadable,corruptBackupCount=Directory.GetFiles(Path.GetDirectoryName(fp)!,Path.GetFileName(fp)+".corrupt-*").Length});

  var life=Host();life.AuditRequest("app.ready");string afterApp=life.LoadingOverlay.Visibility.ToString();
  life.AuditRequest("book.begin",new(){["loadId"]="manual-open"});life.AuditRequest("book.error",new(){["loadId"]="manual-open",["message"]="synthetic failure"});
  life.AuditRequest("book.begin",new(){["loadId"]="manual-retry"});life.AuditRequest("book.ready",new(){["loadId"]="manual-retry"});
  Record("H-ERROR-OVERLAY",life.AuditReady&&life.LoadingOverlay.Visibility==Visibility.Visible,new{afterApp,afterReady=life.LoadingOverlay.Visibility.ToString(),text=life.LoadingText.Text,ready=life.AuditReady});
  var foreign=Host();foreign.AuditRaw("\"lyra-book-ready\"","https://other.invalid/old.html");
  Record("H-LEGACY-SOURCE",foreign.AuditReady,new{ready=foreign.AuditReady,overlay=foreign.LoadingOverlay.Visibility.ToString()});
  var missing=Host();missing.AuditRequest("book.begin",new(){["loadId"]="new-open"});missing.AuditRequest("book.ready");
  Record("H-MISSING-LOAD",missing.AuditReady,new{currentLoad=missing.AuditLoad,ready=missing.AuditReady});

  var refresh=Host();string oldSession=refresh.AuditSession;refresh.Wv.CoreWebView2.AuditNavigation(true);
  var oldSessionWrite=refresh.AuditRequest("state.savePreferences",new(){["preferences"]=new JsonObject{["fontSize"]=25}},oldSession);
  Record("H-REFRESH-SESSION",oldSession==refresh.AuditSession&&Ok(oldSessionWrite),new{sameSession=oldSession==refresh.AuditSession,oldSessionWrite,note="NavigationCompleted delivered; source has no NavigationStarting handler/session rotation on browser refresh"});
  var failNav=Host();failNav.Wv.CoreWebView2.AuditNavigation(false);string errorBefore=failNav.LoadingOverlay.Visibility.ToString();
  foreach(var t in DispatcherTimer.All.ToArray())t.AuditTick();
  Record("H-EMPTY-NAV-FAIL",failNav.LoadingOverlay.Visibility==Visibility.Collapsed,new{beforeTimer=errorBefore,afterTimer=failNav.LoadingOverlay.Visibility.ToString(),text=failNav.LoadingText.Text});

  var closing=Host();closing.Wv.CoreWebView2.AuditNavigation(true);int timersBefore=DispatcherTimer.All.Count(t=>t.Running);int repliesBefore=closing.Wv.CoreWebView2.Replies.Count;
  closing.Close();int timersAfter=DispatcherTimer.All.Count(t=>t.Running);
  Record("H-CLOSE-STRUCTURE",timersAfter>0,new{timersBefore,timersAfter,outboundMessagesAtClose=closing.Wv.CoreWebView2.Replies.Count-repliesBefore,note="Only timer/host control-flow observation; no native WinUI shutdown or pagehide durability simulated"});

  // Independent store instances have independent locks. These calls use workers intentionally;
  // actual WinUI UI-thread message dispatch may serialize synchronous calls, so report that limit.
  var race1=new ReaderStateStore();var race2=new ReaderStateStore();int lost=0,errors=0;var prefsFile=Path.Combine(s.Root,"preferences.json");
  for(int i=0;i<60;i++){
   File.WriteAllText(prefsFile,"{\"version\":1,\"revision\":0,\"preferences\":{}}");using var start=new Barrier(2);
   var x=Task.Run(()=>{start.SignalAndWait();try{race1.SavePreferences("one",Json("{\"fontSize\":29}"));}catch{Interlocked.Increment(ref errors);}});
   var y=Task.Run(()=>{start.SignalAndWait();try{race2.SavePreferences("two",Json("{\"theme\":\"sepia\"}"));}catch{Interlocked.Increment(ref errors);}});Task.WaitAll(x,y);
   var p=race1.Load(bc,"observer")["preferences"]!;if(p["fontSize"]==null||p["theme"]==null)lost++;
  }
  Record("H-STORE-LOCKS",lost>0,new{iterations=60,lostIndependentFieldUpdates=lost,errors,note="Concurrent store API calls, not proof of parallel WinUI UI dispatch"});
  File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"host-results.json"),JsonSerializer.Serialize(new{generatedAt=DateTime.UtcNow,baseDirectory=AppContext.BaseDirectory,evidenceKind="Original C# source with headless event doubles; not desktop integration",results=Results},new JsonSerializerOptions{WriteIndented=true}));
 }
}
