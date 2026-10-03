// Compile-only stand-ins for the few WPF types Models.cs touches. Never run.
namespace System.Windows
{
    public class Application { public static Application Current => null!; public object FindResource(object k) => null!; public object? TryFindResource(object k) => null; }
    public struct Point { public Point(double x, double y) { X = x; Y = y; } public double X; public double Y; }
}
namespace System.Windows.Media
{
    public class Brush { }
    public class SolidColorBrush : Brush { }
    public class ImageSource { }
    public class PointCollection : System.Collections.Generic.List<System.Windows.Point> { public void Freeze() { } }
}
namespace System.Diagnostics
{
    public enum EventLogEntryType { Error = 1, Warning = 2, Information = 4, SuccessAudit = 8, FailureAudit = 16 }
    public class EventSourceCreationData { public EventSourceCreationData(string s, string l) { } }
    public class EventLog : IDisposable
    {
        public EventLog(string log) { } public EventLog(string log, string machine, string source) { }
        public string Source { get; set; } = "";
        public static bool SourceExists(string s) => false;
        public static void CreateEventSource(EventSourceCreationData d) { }
        public static void CreateEventSource(string s, string l) { }
        public void WriteEntry(string m, EventLogEntryType t, int id) { }
        public void WriteEntry(string m, EventLogEntryType t) { }
        public void Dispose() { }
    }
}
