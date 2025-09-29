using System;

namespace Common
{
    public class TransferStartedEventArgs : EventArgs
    {
        public string VehicleId { get; set; }
        public DateTime UtcStarted { get; set; }

        public TransferStartedEventArgs() { }
        public TransferStartedEventArgs(string vehicleId, DateTime utcStarted)
        {
            VehicleId = vehicleId;
            UtcStarted = utcStarted;
        }
    }

    public class SampleReceivedEventArgs : EventArgs
    {
        public string VehicleId { get; set; }
        public int RowIndex { get; set; }
        public DateTime Timestamp { get; set; }

        public SampleReceivedEventArgs() { }
        public SampleReceivedEventArgs(string vehicleId, int rowIndex, DateTime timestamp)
        {
            VehicleId = vehicleId;
            RowIndex = rowIndex;
            Timestamp = timestamp;
        }
    }

    public class TransferCompletedEventArgs : EventArgs
    {
        public string VehicleId { get; set; }
        public int AcceptedCount { get; set; }
        public int RejectedCount { get; set; }
        public DateTime UtcCompleted
        {
            get; set; }

        public TransferCompletedEventArgs() { }
        public TransferCompletedEventArgs(string vehicleId, int acceptedCount, int rejectedCount, DateTime utcCompleted)
        {
            VehicleId = vehicleId;
            AcceptedCount = acceptedCount;
            RejectedCount = rejectedCount;
            UtcCompleted = utcCompleted;
        }
    }

    public class WarningEventArgs : EventArgs
    {
        public string VehicleId { get; set; }
        public int? RowIndex { get; set; }
        public string Reason { get; set; }
        public DateTime UtcRaised { get; set; }

        public WarningEventArgs() { }
        public WarningEventArgs(string vehicleId, int? rowIndex, string reason, DateTime utcRaised)
        {
            VehicleId = vehicleId;
            RowIndex = rowIndex;
            Reason = reason;
            UtcRaised = utcRaised;
        }
    }

    public class FrequencyDeviationEventArgs : EventArgs
    {
        public string VehicleId { get; set; }
        public int? RowIndex { get; set; }
        public double FrequencyAvg { get; set; }
        public double DeviationHz { get; set; }   
        public double LimitHz { get; set; }      
        public DateTime UtcRaised { get; set; }
    }

    public class FrequencySpikeEventArgs : EventArgs
    {
        public string VehicleId { get; set; }
        public int? RowIndex { get; set; }
        public double DeltaMinHz { get; set; }    
        public double DeltaMaxHz { get; set; }    
        public double ThresholdHz { get; set; }
        public DateTime UtcRaised { get; set; }
    }
}