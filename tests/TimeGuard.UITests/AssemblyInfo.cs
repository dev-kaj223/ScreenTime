using Xunit;

// Profiles/processes are isolated, but UI tests still share desktop keyboard/mouse input.
// Keep those interactions serial.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
