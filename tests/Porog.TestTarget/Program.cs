// Harmless target for the Windows process-blocking smoke test. Never touches user data.
Console.WriteLine("Porog integration test target");
await Task.Delay(Timeout.InfiniteTimeSpan);
