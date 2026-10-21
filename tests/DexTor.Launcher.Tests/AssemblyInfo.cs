using Xunit;

// Launcher tests replace process-wide callbacks and create WinForms controls.
// Running them in parallel can leak a real modal callback into another test.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
