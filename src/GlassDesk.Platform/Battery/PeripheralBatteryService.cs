using Windows.Devices.Enumeration;
namespace GlassDesk.Platform.Battery;
/// <summary>Shared serial discovery and sampling; consumers own activation, never its lifetime.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification="Process lifetime shared service; SetActive(false) cancels workers and stops watcher.")]
public sealed class PeripheralBatteryService
{
 public static PeripheralBatteryService Shared { get; } = new();
 private readonly SemaphoreSlim _queryGate = new(1,1);
 private readonly object _stateGate=new();
 private readonly IPeripheralBatteryProvider[] _providers;
 private CancellationTokenSource? _active;
 private DeviceWatcher? _watcher;
 private Timer? _notifications;
 private Task? _unfinishedProvider;
 private long _generation;
 private IReadOnlyList<PeripheralBatterySnapshot> _current = [];
 private PeripheralBatteryCategory[] _categories = Enum.GetValues<PeripheralBatteryCategory>();
 public bool IsActive {get {lock(_stateGate)return _active is not null;}}
 public IReadOnlyList<PeripheralBatterySnapshot> Current => Volatile.Read(ref _current);
 public event EventHandler? Updated;
 public PeripheralBatteryService() : this([new NativeHidBatteryProvider(),new WindowsPeripheralBatteryProvider(),new MyFinderBatteryProvider(),new HeadsetControlBatteryProvider()]) { }
 internal PeripheralBatteryService(IPeripheralBatteryProvider[] providers) { _providers=providers; }
 public void SetCategories(IReadOnlyCollection<PeripheralBatteryCategory> categories) {
  var next=categories.Distinct().Order().ToArray();bool restart;
  lock(_stateGate) {if(_categories.SequenceEqual(next))return;_categories=next;restart=_active is not null;Interlocked.Increment(ref _generation);}
  if(restart) {SetActive(false);SetActive(true);}
 }
 public void SetActive(bool active)
 {
  lock(_stateGate) {
   if(active==(_active is not null))return;
   Interlocked.Increment(ref _generation);
   var previous=_active;_active=null;previous?.Cancel();
   foreach(var engine in _providers.OfType<MyFinderBatteryProvider>())engine.SetActive(active);
   StopWatcher();
   if(active) {_active=new();StartWatcher();_=PollAsync(_active);}
  }
 }
 private void StartWatcher() {
  try {
   _notifications=new Timer(_=>NotificationRefresh(),null,Timeout.Infinite,Timeout.Infinite);
   _watcher=DeviceInformation.CreateWatcher(DeviceClass.All);
   _watcher.Added+=DeviceAdded;_watcher.Removed+=DeviceChanged;_watcher.Updated+=DeviceChanged;_watcher.Start();
  }catch(Exception ex)when(ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException) {StopWatcher();}
 }
 private void StopWatcher() {
  _notifications?.Dispose();_notifications=null;
  if(_watcher is not {} watcher)return;_watcher=null;
  watcher.Added-=DeviceAdded;watcher.Removed-=DeviceChanged;watcher.Updated-=DeviceChanged;
  if(watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)watcher.Stop();
 }
 private void DeviceAdded(DeviceWatcher sender,DeviceInformation args)=>QueueNotification();
 private void DeviceChanged(DeviceWatcher sender,DeviceInformationUpdate args)=>QueueNotification();
 private void QueueNotification() {lock(_stateGate) {if(_watcher?.Status==DeviceWatcherStatus.EnumerationCompleted)_notifications?.Change(1500,Timeout.Infinite);}}
 private async void NotificationRefresh() {
  CancellationToken token;lock(_stateGate){if(_active is null)return;token=_active.Token;}
  try {await RefreshCoreAsync(false,token).ConfigureAwait(false);}catch(OperationCanceledException){}catch(Exception ex)when(ex is not OutOfMemoryException){System.Diagnostics.Debug.WriteLine(ex);}
 }
 private async Task PollAsync(CancellationTokenSource lifetime) {
  try {do {await RefreshCoreAsync(false,lifetime.Token).ConfigureAwait(false);await Task.Delay(TimeSpan.FromSeconds(60),lifetime.Token).ConfigureAwait(false);}while(!lifetime.IsCancellationRequested);}
  catch(OperationCanceledException){}finally {lifetime.Dispose();}
 }
 public Task<IReadOnlyList<PeripheralBatterySnapshot>> RefreshAsync(CancellationToken cancellationToken=default)=>RefreshCoreAsync(true,cancellationToken);
 private async Task<IReadOnlyList<PeripheralBatterySnapshot>> RefreshCoreAsync(bool discovery,CancellationToken cancellationToken)
 {
  var generation=Volatile.Read(ref _generation);
  await _queryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
  try {
   // A provider ignoring cancellation cannot overlap a later query. Its late result is never published.
   if(_unfinishedProvider is {IsCompleted:false})return Current;
   _unfinishedProvider=null;
   var categories=discovery?Enum.GetValues<PeripheralBatteryCategory>():Volatile.Read(ref _categories);
   var readings=new List<PeripheralBatterySnapshot>();
   foreach(var provider in _providers) {
    cancellationToken.ThrowIfCancellationRequested();
    if(provider is HeadsetControlBatteryProvider&&!categories.Contains(PeripheralBatteryCategory.Headset))continue;
    if(provider is NativeHidBatteryProvider hid)hid.Categories=categories;
    if(provider is WindowsPeripheralBatteryProvider windows)windows.Categories=categories;
    if(provider is MyFinderBatteryProvider engine)engine.Categories=categories;
    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(provider is MyFinderBatteryProvider?45:15));
    Task<IReadOnlyList<PeripheralBatterySnapshot>>? pending=null;
    try {pending=provider.QueryAsync(timeout.Token);readings.AddRange(await pending.WaitAsync(timeout.Token).ConfigureAwait(false));}
    catch(OperationCanceledException) {
     if(pending is {IsCompleted:false}) {_unfinishedProvider=pending;_=pending.ContinueWith(t=>System.Diagnostics.Debug.WriteLine(t.Exception),CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);}
     cancellationToken.ThrowIfCancellationRequested();
     if(_unfinishedProvider is not null)break;
    }
    catch(Exception ex)when(ex is not OutOfMemoryException) {System.Diagnostics.Debug.WriteLine(ex);}
   }
   cancellationToken.ThrowIfCancellationRequested();
   if(generation!=Volatile.Read(ref _generation))return Current;
   Volatile.Write(ref _current,Merge(readings));
   Updated?.Invoke(this,EventArgs.Empty);return Current;
  }finally {_queryGate.Release();}
 }
 public static IReadOnlyList<PeripheralBatterySnapshot> Merge(IEnumerable<PeripheralBatterySnapshot> devices) {
  var all=devices.Select(Fresh).ToArray();
  // Only an exact physical/category match with one logical engine unit proves an unslotted
  // native reading is the same device. Keep its established key for existing manual bindings.
  var canonical=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  foreach(var native in all.Where(d=>d.Source.StartsWith("HID ",StringComparison.Ordinal)&&!d.ReceiverSlot.HasValue&&!string.IsNullOrEmpty(d.PhysicalDeviceId)
   &&string.Equals(d.DeviceKey,$"hid:{d.PhysicalDeviceId}:{d.Category.ToString().ToLowerInvariant()}",StringComparison.OrdinalIgnoreCase))) {
   var engine=all.Where(d=>d.Source.StartsWith("MyFinder ",StringComparison.Ordinal)&&SamePhysical(d,native)).ToArray();
   if(engine.Length==1&&!engine[0].ReceiverSlot.HasValue)canonical[engine[0].DeviceKey]=native.DeviceKey;
  }
  return all.Where(d=>d.ReceiverSlot.HasValue || d.Source!="Windows HID" || !all.Any(other=>(other.ReceiverSlot.HasValue || (other.Source.StartsWith("MyFinder ",StringComparison.Ordinal) && other.DeviceKey!=d.DeviceKey)) && SamePhysical(other,d)))
   .Select(d=>canonical.TryGetValue(d.DeviceKey,out var key)?d with {DeviceKey=key}:d)
   .GroupBy(d=>d.DeviceKey,StringComparer.OrdinalIgnoreCase)
   .Select(g=>g.OrderBy(d=>ReadingPriority(d)).First() with {IsDefaultAudio=g.Any(d=>d.IsDefaultAudio)||all.Any(d=>d.IsDefaultAudio&&SamePhysical(d,g.First()))})
   .OrderBy(d=>d.Category).ThenBy(d=>d.DeviceKey,StringComparer.OrdinalIgnoreCase).ToArray();
 }
 private static bool SamePhysical(PeripheralBatterySnapshot a,PeripheralBatterySnapshot b)=>a.Category==b.Category&&!string.IsNullOrEmpty(a.PhysicalDeviceId)&&string.Equals(a.PhysicalDeviceId,b.PhysicalDeviceId,StringComparison.OrdinalIgnoreCase);
 private static int ReadingPriority(PeripheralBatterySnapshot d)=>(d.Connected&&(d.Percent.HasValue||d.Level is not null)?0:d.Connected?10:20)+(d.Source.StartsWith("HID ",StringComparison.Ordinal)?0:d.Source.StartsWith("MyFinder ",StringComparison.Ordinal)?1:d.Source=="Windows HID"?4:2);
 private static PeripheralBatterySnapshot Fresh(PeripheralBatterySnapshot snapshot) => !snapshot.Connected || DateTimeOffset.UtcNow-snapshot.SampleTime>TimeSpan.FromMinutes(2)
  ? snapshot with {Percent=null,Level=null,Charging=null,Reason=!snapshot.Connected?snapshot.Reason:"电量读数已过期，等待刷新"}:snapshot;
 public static PeripheralBatterySnapshot? Select(IEnumerable<PeripheralBatterySnapshot> devices,PeripheralBatteryCategory category,string? preferredKey,string? retainedAutomaticKey=null) {
  var candidates=devices.Where(d=>d.Category==category).Select(Fresh).ToArray();
  if(!string.IsNullOrWhiteSpace(preferredKey))return candidates.FirstOrDefault(d=>string.Equals(d.DeviceKey,preferredKey,StringComparison.OrdinalIgnoreCase));
  var connected=candidates.Where(d=>d.Connected).OrderByDescending(d=>d.Percent.HasValue||d.Level is not null).ThenBy(d=>d.DeviceKey,StringComparer.OrdinalIgnoreCase).ToArray();
  if(category==PeripheralBatteryCategory.Headset&&connected.FirstOrDefault(d=>d.IsDefaultAudio) is {} primary)return primary;
  return connected.FirstOrDefault(d=>string.Equals(d.DeviceKey,retainedAutomaticKey,StringComparison.OrdinalIgnoreCase))??connected.FirstOrDefault();
 }
}
