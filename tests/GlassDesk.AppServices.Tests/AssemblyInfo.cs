using GlassDesk.AppServices.Tests;

// Run the tests in this assembly one at a time.
//
// WHY: ShortcutResolverTests.连续解析两百次不泄漏GDI与USER句柄 measures GetGuiResources on the
// current process before and after 200 resolutions. A process-wide counter is only a valid
// measurement when nothing else in that process is creating GDI or USER objects at the same
// time, and xUnit's default is to run test classes in parallel inside one process. On the
// development machine the races happened to stay under the tolerance; in the test virtual
// machine the same test measured 0 -> 41 GDI handles and failed, which is a defect in the
// measurement rather than in the product.
//
// Serializing this assembly costs a few seconds (the whole suite runs in about 3 s) and makes
// the leak assertion mean what it says. A real leak — one handle per resolution — still fails
// it with a delta near 200.
[assembly: CollectionBehavior(DisableTestParallelization = true)]