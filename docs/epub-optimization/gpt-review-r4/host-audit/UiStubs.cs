// Diagnostic-only WinUI/WebView2 event doubles. Original product files are linked verbatim.
// This executable does not create a desktop window or browser. Not an integration test.
using System.Text.Json.Nodes;
namespace Windows.Graphics {
 public struct RectInt32 { public int X,Y,Width,Height; public RectInt32(int x,int y,int w,int h){X=x;Y=y;Width=w;Height=h;} }
 public struct SizeInt32 { public SizeInt32(int w,int h){} }
}
namespace Microsoft.UI.Windowing {
 public enum DisplayAreaFallback { Primary }
 public class DisplayArea { public Windows.Graphics.RectInt32 WorkArea => new(0,0,1400,900); public static DisplayArea GetFromWindowId(object id, DisplayAreaFallback f)=>new(); }
 public class AppWindowClosingEventArgs { public bool Cancel {get;set;} }
 public class AppWindow { public object Id {get;}=new(); public void MoveAndResize(Windows.Graphics.RectInt32 r){} public void Resize(Windows.Graphics.SizeInt32 s){} public event Action<AppWindow,AppWindowClosingEventArgs>? Closing; public void AuditClosing(){var e=new AppWindowClosingEventArgs();Closing?.Invoke(this,e);} }
}
namespace Microsoft.UI.Dispatching { public class DispatcherQueue { public bool TryEnqueue(Action action){action();return true;} } }
namespace Microsoft.UI.Xaml {
 using Microsoft.UI.Dispatching;
 public enum Visibility { Visible, Collapsed }
 public enum WindowActivationState { CodeActivated, Deactivated, PointerActivated }
 public class WindowActivatedEventArgs : EventArgs { public WindowActivationState WindowActivationState {get;set;} }
 public class RoutedEventArgs : EventArgs {}
 public class Window {
  public string Title {get;set;}=""; public Microsoft.UI.Windowing.AppWindow AppWindow {get;}=new();
  public DispatcherQueue DispatcherQueue {get;}=new();
  public event EventHandler<WindowActivatedEventArgs>? Activated; public event EventHandler<EventArgs>? Closed;
  public void AuditActivate(WindowActivationState state)=>Activated?.Invoke(this,new(){WindowActivationState=state});
  public int ClosedCount { get; private set; }
   public void Close(){ ClosedCount++; Closed?.Invoke(this,EventArgs.Empty); }
 }
 public class DispatcherTimer {
  public static List<DispatcherTimer> All {get;}=new(); public TimeSpan Interval {get;set;} public bool Running {get;private set;}
  public event EventHandler<object>? Tick; public DispatcherTimer(){All.Add(this);} public void Start()=>Running=true; public void Stop()=>Running=false;
  public void AuditTick(){if(Running)Tick?.Invoke(this,new object());}
 }
 public class Control { public Visibility Visibility {get;set;}=Visibility.Visible; public string Text {get;set;}=""; }
}
namespace Microsoft.UI.Xaml.Input { public class Dummy {} }
namespace Microsoft.Web.WebView2.Core {
 public enum CoreWebView2HostResourceAccessKind { Allow }
 public enum CoreWebView2NavigationKind { NewDocument, Reload }
 public class CoreWebView2NavigationCompletedEventArgs : EventArgs { public bool IsSuccess {get;set;} public string WebErrorStatus {get;set;}="SyntheticFailure"; }
 public class CoreWebView2NavigationStartingEventArgs : EventArgs { public bool Cancel {get;set;} public CoreWebView2NavigationKind NavigationKind {get;set;} }
 public class CoreWebView2WebMessageReceivedEventArgs : EventArgs { public string WebMessageAsJson {get;set;}=""; public string Source {get;set;}=""; }
 public class CoreWebView2Settings { public bool IsWebMessageEnabled {get;set;} }
 public class CoreWebView2 {
  public CoreWebView2Settings Settings {get;}=new(); public List<string> Replies {get;}=new(); public string LastNavigation {get;private set;}="";
  public event Action<CoreWebView2,CoreWebView2WebMessageReceivedEventArgs>? WebMessageReceived;
  public event Action<CoreWebView2,CoreWebView2NavigationCompletedEventArgs>? NavigationCompleted;
  public event Action<CoreWebView2,CoreWebView2NavigationStartingEventArgs>? NavigationStarting;
  public void SetVirtualHostNameToFolderMapping(string host,string folder,CoreWebView2HostResourceAccessKind kind){}
  public void Navigate(string url){LastNavigation=url; AuditNavigationStarting(CoreWebView2NavigationKind.NewDocument);} public void PostWebMessageAsJson(string json)=>Replies.Add(json);
  public void AuditMessage(string raw,string source)=>WebMessageReceived?.Invoke(this,new(){WebMessageAsJson=raw,Source=source});
  public void AuditNavigation(bool success)=>NavigationCompleted?.Invoke(this,new(){IsSuccess=success});
  public bool AuditNavigationStarting(CoreWebView2NavigationKind kind){var e=new CoreWebView2NavigationStartingEventArgs{NavigationKind=kind};NavigationStarting?.Invoke(this,e);return e.Cancel;}
 }
}
namespace WinUIGui {
 public class App {}
 public class FakeWebView { public Microsoft.Web.WebView2.Core.CoreWebView2 CoreWebView2 {get; private set;}=new(); private TaskCompletionSource? gate; public int EnsureCalls; public void HoldEnsure(){CoreWebView2=null!;gate=new();} public void ReleaseEnsure(){CoreWebView2=new();gate?.SetResult();} public Task EnsureCoreWebView2Async(){EnsureCalls++;return gate?.Task??Task.CompletedTask;} }
 public sealed partial class ExtensionHostWindow {
  public FakeWebView Wv {get;}=new();
  public Microsoft.UI.Xaml.Control LoadingOverlay=new(), LoadingText=new(), LoadingDetail=new(), LoadingRetry=new(), LoadingCancel=new(), LoadingRing=new();
  private void InitializeComponent(){}
  public void AuditInitialize()=>Root_Loaded(this,new());
  public string AuditSession=>_sessionId; public string AuditLoad=>_loadId; public string AuditOrigin=>Wv.CoreWebView2.LastNavigation;
   public long AuditActivation=>_activationEpoch; public long AuditNav=>_navEpoch;
   public void AuditSetStoreUnavailable()=>_store=null;
   public JsonObject AuditEpoch(string book)=>new(){["bookId"]=book,["activationEpoch"]=_activationEpoch,["navEpoch"]=_navEpoch};
  public bool AuditActive=>_isActive; public bool AuditReady=>_bookReady;
   public bool AuditFinalizePending=>_finalizePending; public string AuditFinalizeRequest=>_finalizeRequestId;
   public bool AuditCloseTimerRunning=>_closeTimer?.Running==true;
   public void AuditTickOwnTimers(){foreach(var t in _timers.ToArray())t.AuditTick();}
   public void AuditStopTimers()=>StopTimers(); public void AuditNavigate()=>NavigateToExtension(); public void AuditCleanup()=>CleanupTemp(); public void AuditNavFail()=>OnNavigationCompleted(Wv.CoreWebView2,new(){IsSuccess=false});
  public int AuditRunningTimers=>Microsoft.UI.Xaml.DispatcherTimer.All.Count(t=>t.Running);
  public void AuditRaw(string raw,string? source=null)=>Wv.CoreWebView2.AuditMessage(raw,source??AuditOrigin);
  public JsonObject? AuditRequest(string type,JsonObject? fields=null,string? session=null,string? source=null){
   var msg=fields??new JsonObject(); msg["protocol"]=ReaderProtocol.Protocol; msg["version"]=ReaderProtocol.Version; msg["type"]=type;
   msg["requestId"]=Guid.NewGuid().ToString("N"); msg["sessionId"]=session??AuditSession;
   int before=Wv.CoreWebView2.Replies.Count; AuditRaw(msg.ToJsonString(new System.Text.Json.JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}),source);
   return Wv.CoreWebView2.Replies.Count>before?JsonNode.Parse(Wv.CoreWebView2.Replies[^1])!.AsObject():null;
  }
 }
}
