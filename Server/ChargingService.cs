using Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceModel;

namespace Service
{
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)]
    public class ChargingService : IChargingService, IDisposable
    {

        private bool _active;
        private string _vehicleId;

        private readonly HashSet<int> _acceptedRows = new HashSet<int>();
        private readonly HashSet<int> _rejectedRows = new HashSet<int>();

        private string _sessionDir;
        private string _sessionCsvPath;
        private string _rejectsCsvPath;

        private FileStream _sessionFs;
        private StreamWriter _sessionWriter;

        private FileStream _rejectsFs;
        private StreamWriter _rejectsWriter;


        public event EventHandler<TransferStartedEventArgs> OnTransferStarted;
        public event EventHandler<SampleReceivedEventArgs> OnSampleReceived;
        public event EventHandler<TransferCompletedEventArgs> OnTransferCompleted;
        public event EventHandler<WarningEventArgs> OnWarningRaised;


        public event EventHandler<FrequencyDeviationEventArgs> OnFrequencyDeviation;
        public event EventHandler<FrequencySpikeEventArgs> OnFrequencySpike;

        private int _acceptedCount;
        private int _rejectedCount;


        private bool _hasPrevTs;
        private DateTime _prevTsUtc;
        private double _cumulativeEnergyKWh;

        
        private int _stallNoGrowthCount;
        private const int StallConsecutiveNoGrowthLimit = 10;

        
        private const double OverloadThresholdKW = 6.0; 

        
        private const double NominalHz = 50.0;
        private const double DeviationLimitHz = 0.5;     
        private const double SpikeThresholdHz = 0.20;    

        private bool _hasPrevFreq;
        private double _prevFmin;
        private double _prevFmax;

        public void StartSession(string vehicleId)
        {
            if (_active) throw Fault("Session already active.");
            if (string.IsNullOrWhiteSpace(vehicleId)) throw Fault("VehicleId is required.");

            _active = true;
            _vehicleId = vehicleId.Trim();

            _acceptedRows.Clear();
            _rejectedRows.Clear();
            _acceptedCount = 0;
            _rejectedCount = 0;

            
            _hasPrevTs = false;
            _cumulativeEnergyKWh = 0.0;
            _stallNoGrowthCount = 0;

            _hasPrevFreq = false;
            _prevFmin = 0.0;
            _prevFmax = 0.0;

            var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
            _sessionDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", _vehicleId, date);
            Directory.CreateDirectory(_sessionDir);

            _sessionCsvPath = Path.Combine(_sessionDir, "session.csv");
            _rejectsCsvPath = Path.Combine(_sessionDir, "rejects.csv");

            bool newSession = !File.Exists(_sessionCsvPath);
            _sessionFs = new FileStream(_sessionCsvPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _sessionWriter = new StreamWriter(_sessionFs) { AutoFlush = true };
            if (newSession)
            {
                _sessionWriter.WriteLine(
                    "RowIndex,Timestamp,VoltMin,VoltAvg,VoltMax," +
                    "CurrMin,CurrAvg,CurrMax," +
                    "RealMin,RealAvg,RealMax," +
                    "ReacMin,ReacAvg,ReacMax," +
                    "AppMin,AppAvg,AppMax," +
                    "FreqMin,FreqAvg,FreqMax," +
                    "VehicleId,E_kWh");
            }

            bool newRejects = !File.Exists(_rejectsCsvPath);
            _rejectsFs = new FileStream(_rejectsCsvPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _rejectsWriter = new StreamWriter(_rejectsFs) { AutoFlush = true };
            if (newRejects)
            {
                _rejectsWriter.WriteLine("RowIndex,Reason,VehicleId");
            }

            Console.WriteLine($"[SERVER] StartSession: {_vehicleId}");
            Console.WriteLine("[SERVER] Status: prenos u toku...");
            RaiseTransferStarted();
        }

        public void PushSample(ChargingSample s)
        {
            EnsureActive();

            
            if (s != null && s.RowIndex > 0 && _acceptedRows.Contains(s.RowIndex))
                return;

            
            if (s == null) Reject("Sample is null.", null);
            if (s.Timestamp == default) Reject("Invalid Timestamp.", s.RowIndex);

            if (!AllStrictlyPositive(s.VoltageRmsMin, s.VoltageRmsAvg, s.VoltageRmsMax))
                Reject("Voltage RMS must be > 0.", s.RowIndex);

            if (!AllNonNegative(s.CurrentRmsMin, s.CurrentRmsAvg, s.CurrentRmsMax))
                Reject("Current RMS must be >= 0.", s.RowIndex);

            if (!AllStrictlyPositive(s.FrequencyMin, s.FrequencyAvg, s.FrequencyMax))
                Reject("Frequency must be > 0.", s.RowIndex);


            double dtHours;
            if (_hasPrevTs)
            {
                dtHours = (s.Timestamp.ToUniversalTime() - _prevTsUtc).TotalHours;
                if (dtHours <= 0) dtHours = 1.0 / 60.0; 
            }
            else
            {
                dtHours = 0.0;
                _hasPrevTs = true;
            }

            
            double dE = s.RealPowerAvg * dtHours; 
            if (dE < 0) dE = 0;                   
            _cumulativeEnergyKWh += dE;

            
            if (dE <= 0.0)
            {
                _stallNoGrowthCount++;
                if (_stallNoGrowthCount > StallConsecutiveNoGrowthLimit)
                {
                    RaiseWarning(
                        $"EnergyStallWarning: cumulative E stagnira > {StallConsecutiveNoGrowthLimit} redova.",
                        s.RowIndex);
                    _stallNoGrowthCount = 0; 
                }
            }
            else
            {
                _stallNoGrowthCount = 0;
            }

            
            if (s.RealPowerMax > OverloadThresholdKW)
            {
                RaiseWarning(
                    $"OverloadWarning: RealPowerMax={s.RealPowerMax:F3} kW > {OverloadThresholdKW} kW",
                    s.RowIndex);
            }


            double dev = Math.Abs(s.FrequencyAvg - NominalHz);
            if (dev > DeviationLimitHz)
            {
                
                OnFrequencyDeviation?.Invoke(this, new FrequencyDeviationEventArgs
                {
                    VehicleId = _vehicleId,
                    RowIndex = s.RowIndex,
                    FrequencyAvg = s.FrequencyAvg,
                    DeviationHz = dev,
                    LimitHz = DeviationLimitHz,
                    UtcRaised = DateTime.UtcNow
                });

                
                RaiseWarning(
                    $"FrequencyDeviationWarning: f_avg={s.FrequencyAvg:F3} Hz, dev={dev:F3} Hz > {DeviationLimitHz:F3} Hz",
                    s.RowIndex);
            }

            
            if (_hasPrevFreq)
            {
                double dfMin = Math.Abs(s.FrequencyMin - _prevFmin);
                double dfMax = Math.Abs(s.FrequencyMax - _prevFmax);
                if (dfMin > SpikeThresholdHz || dfMax > SpikeThresholdHz)
                {
                    OnFrequencySpike?.Invoke(this, new FrequencySpikeEventArgs
                    {
                        VehicleId = _vehicleId,
                        RowIndex = s.RowIndex,
                        DeltaMinHz = dfMin,
                        DeltaMaxHz = dfMax,
                        ThresholdHz = SpikeThresholdHz,
                        UtcRaised = DateTime.UtcNow
                    });

                    RaiseWarning(
                        $"FrequencySpikeWarning: Δf_min={dfMin:F3} Hz, Δf_max={dfMax:F3} Hz (prag={SpikeThresholdHz:F3} Hz)",
                        s.RowIndex);
                }
            }
            _prevFmin = s.FrequencyMin;
            _prevFmax = s.FrequencyMax;
            _hasPrevFreq = true;

            
            _prevTsUtc = s.Timestamp.ToUniversalTime();

            string line = string.Join(",",
                s.RowIndex,
                s.Timestamp.ToString("o"),
                s.VoltageRmsMin, s.VoltageRmsAvg, s.VoltageRmsMax,
                s.CurrentRmsMin, s.CurrentRmsAvg, s.CurrentRmsMax,
                s.RealPowerMin, s.RealPowerAvg, s.RealPowerMax,
                s.ReactivePowerMin, s.ReactivePowerAvg, s.ReactivePowerMax,
                s.ApparentPowerMin, s.ApparentPowerAvg, s.ApparentPowerMax,
                s.FrequencyMin, s.FrequencyAvg, s.FrequencyMax,
                _vehicleId,
                _cumulativeEnergyKWh.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );

            _sessionWriter.WriteLine(line);
            _acceptedRows.Add(s.RowIndex);
            _acceptedCount++;
            RaiseSampleReceived(s.RowIndex, s.Timestamp);

            if (s.RowIndex % 100 == 0)
                Console.WriteLine($"[SERVER] primljeno {s.RowIndex} redova...");
        }

        public void EndSession(string vehicleId)
        {
            EnsureActive();
            if (!string.Equals(vehicleId?.Trim(), _vehicleId, StringComparison.OrdinalIgnoreCase))
                throw Fault("VehicleId mismatch.");

            Console.WriteLine($"[SERVER] EndSession: {vehicleId}");
            Console.WriteLine("[SERVER] Status: prenos završen.");
            RaiseTransferCompleted();

            _active = false;
            CloseSessionWriters();
        }

       
        private static bool AllStrictlyPositive(params double[] vals) => vals.All(v => v > 0.0);
        private static bool AllNonNegative(params double[] vals) => vals.All(v => v >= 0.0);

        private void EnsureActive()
        {
            if (!_active) Reject("No active session.", null);
        }

        private void Reject(string reason, int? rowIndex)
        {
            if (rowIndex.HasValue && _rejectedRows.Add(rowIndex.Value))
            {
                _rejectsWriter?.WriteLine($"{rowIndex},{reason},{_vehicleId}");
                _rejectedCount++;
            }
            RaiseWarning(reason, rowIndex);
            throw Fault(reason, rowIndex);
        }

        private FaultException<FaultInfo> Fault(string reason, int? rowIndex = null)
            => new FaultException<FaultInfo>(new FaultInfo(reason, rowIndex, _vehicleId), reason);

        private void CloseSessionWriters()
        {
            try { _sessionWriter?.Flush(); } catch { }
            try { _rejectsWriter?.Flush(); } catch { }

            try { _sessionWriter?.Dispose(); } catch { }
            try { _rejectsWriter?.Dispose(); } catch { }

            try { _sessionFs?.Dispose(); } catch { }
            try { _rejectsFs?.Dispose(); } catch { }

            _sessionWriter = null;
            _rejectsWriter = null;
            _sessionFs = null;
            _rejectsFs = null;
        }

       
        private void RaiseTransferStarted()
            => OnTransferStarted?.Invoke(this, new TransferStartedEventArgs
            {
                VehicleId = _vehicleId,
                UtcStarted = DateTime.UtcNow
            });

        private void RaiseSampleReceived(int rowIndex, DateTime ts)
            => OnSampleReceived?.Invoke(this, new SampleReceivedEventArgs
            {
                VehicleId = _vehicleId,
                RowIndex = rowIndex,
                Timestamp = ts
            });

        private void RaiseTransferCompleted()
            => OnTransferCompleted?.Invoke(this, new TransferCompletedEventArgs
            {
                VehicleId = _vehicleId,
                AcceptedCount = _acceptedCount,
                RejectedCount = _rejectedCount,
                UtcCompleted = DateTime.UtcNow
            });

        private void RaiseWarning(string reason, int? rowIndex)
            => OnWarningRaised?.Invoke(this, new WarningEventArgs
            {
                VehicleId = _vehicleId,
                RowIndex = rowIndex,
                Reason = reason,
                UtcRaised = DateTime.UtcNow
            });

        
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CloseSessionWriters();
            GC.SuppressFinalize(this);
        }
    }
}
