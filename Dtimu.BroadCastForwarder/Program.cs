using MVCDtimu;
using System.Text.Json.Serialization;

namespace Dtimu.BroadCastForwarder
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine($"[Main {DateTime.Now:HH:mm:ss.fff}] program start, pid={Environment.ProcessId}");

            using var fw = new Forwarder();
            fw.Start();

            using var exitEvent = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Console.WriteLine($"[Main {DateTime.Now:HH:mm:ss.fff}] Ctrl+C received, exiting...");
                exitEvent.Set();
            };

            Console.WriteLine($"[Main {DateTime.Now:HH:mm:ss.fff}] Forwarder started. Press Ctrl+C to exit.");
            exitEvent.Wait();

            Console.WriteLine($"[Main {DateTime.Now:HH:mm:ss.fff}] bye");
        }
    }
}