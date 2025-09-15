using Common;
using System;
using System.ServiceModel;

namespace Service
{
    internal class Program
    {
        static void Main()
        {
            using (var host = new ServiceHost(typeof(ChargingService)))
            {
                host.Open();
                Console.WriteLine("WCF service started. Press Enter to stop...");
                Console.ReadLine();
                host.Close();
            }
        }
    }
}
