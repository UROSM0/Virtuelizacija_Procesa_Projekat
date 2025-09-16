using System;
using System.ServiceModel;
using Common;

namespace Service
{
    internal class Program
    {
        static void Main()
        {
            var svc = new ChargingService();

     
            svc.OnTransferStarted += (s, e) =>
                Console.WriteLine($"[EVT] Started: {e.VehicleId} @ {e.UtcStarted:O}");

            svc.OnSampleReceived += (s, e) =>
                Console.WriteLine($"[EVT] Sample: row={e.RowIndex} ts={e.Timestamp:O} vehicle={e.VehicleId}");

            svc.OnWarningRaised += (s, e) =>
                Console.WriteLine($"[EVT][WARN] row={e.RowIndex?.ToString() ?? "-"} {e.Reason} (veh={e.VehicleId})");

            svc.OnTransferCompleted += (s, e) =>
                Console.WriteLine($"[EVT] Completed: {e.VehicleId} (accepted={e.AcceptedCount}, rejected={e.RejectedCount}) @ {e.UtcCompleted:O}");

            using (var host = new ServiceHost(svc))
            {
                host.Open();
                Console.WriteLine("WCF service started. Press Enter to stop...");
                Console.ReadLine();
                host.Close();
            }
        }
    }
}