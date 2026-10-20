using Xunit;

// Dock tests create WPF windows and replace process-wide DockSettingsHost.Current.
// Running test classes in parallel makes one class observe another class's host.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
