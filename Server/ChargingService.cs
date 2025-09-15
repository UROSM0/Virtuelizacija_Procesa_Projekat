using Common;
using System;
using System.ServiceModel;

namespace Service
{
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)] 
    public class ChargingService : IChargingService
    {
        private bool _active;
        private string _vehicleId;

        public void StartSession(string vehicleId)
        {
            if (_active) throw new InvalidOperationException("Session already active.");
            _active = true;
            _vehicleId = vehicleId?.Trim();
            Console.WriteLine($"[SERVER] StartSession: {_vehicleId}");
        }

        public void PushSample(ChargingSample sample)
        {
            if (!_active) throw new InvalidOperationException("No active session.");
            Console.WriteLine($"[SERVER] Row={sample.RowIndex} Vehicle={_vehicleId} ts={sample.Timestamp:o}");
        }

        public void EndSession(string vehicleId)
        {
            if (!_active) throw new InvalidOperationException("No active session.");
            if (!string.Equals(vehicleId?.Trim(), _vehicleId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("VehicleId mismatch.");
            _active = false;
            Console.WriteLine($"[SERVER] EndSession: {vehicleId}");
        }
    }
}
