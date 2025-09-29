using Common;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.ServiceModel;

namespace Client
{
    class Program
    {
        static void Main(string[] args)
        {
            
            Console.Write("Simuliraj prekid posle N redova? (0 = ne): ");
            int.TryParse(Console.ReadLine(), out int failAfter);

            var vehiclesRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vehicles");
            if (!Directory.Exists(vehiclesRoot))
            {
                Console.WriteLine($"[ERR] Ne postoji folder: {vehiclesRoot}");
                return;
            }

            var files = Directory.GetFiles(vehiclesRoot, "*.*", SearchOption.TopDirectoryOnly)
                                 .Where(p => p.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                                          || p.EndsWith(".tab", StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(Path.GetFileName)
                                 .ToList();
            if (files.Count == 0)
            {
                Console.WriteLine("[ERR] Nije pronađen nijedan .csv/.tab fajl u 'vehicles'.");
                return;
            }

            Console.WriteLine("=== Izaberite vozilo (CSV fajl) ===");
            for (int i = 0; i < files.Count; i++)
                Console.WriteLine($"{i + 1}. {Path.GetFileName(files[i])}");
            Console.Write("Unesite broj: ");
            if (!int.TryParse(Console.ReadLine(), out int choice) || choice < 1 || choice > files.Count)
            {
                Console.WriteLine("[ERR] Pogrešan izbor.");
                return;
            }

            var csvPath = files[choice - 1];
            var vehicleId = Path.GetFileNameWithoutExtension(csvPath);

            string rejectsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rejects.csv");
            if (File.Exists(rejectsPath)) File.Delete(rejectsPath);

            
            using (var svc = new ChargingServiceClient("ChargingEndpoint"))
            {
                
                svc.SafeCall(() => svc.Proxy.StartSession(vehicleId),
                             onFault: reason => Console.WriteLine($"[ERR] StartSession fault: {reason}"),
                             onOk: () => Console.WriteLine($"[OK] StartSession: {vehicleId}"));

                var culture = CultureInfo.InvariantCulture;
                int row = 0;

                try
                {
                    using (var fs = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var sr = new StreamReader(fs))
                    {
                        string firstLine = sr.ReadLine();
                        if (firstLine == null) throw new InvalidOperationException("CSV je prazan.");

                        string delim = GuessDelimiter(firstLine);
                        string[] cols = SplitLine(firstLine, delim);

                        if (!HeaderLooksLikeData(cols))
                        {
                            Console.WriteLine("[INFO] Header prepoznat i preskočen.");
                        }
                        else
                        {
                            row++;
                            TrySendRow(svc, rejectsPath, row, firstLine, cols, vehicleId, culture);
                            if (failAfter > 0 && row >= failAfter) SimulateDrop(svc);
                        }

                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            row++;
                            var parts = SplitLine(line, delim);
                            TrySendRow(svc, rejectsPath, row, line, parts, vehicleId, culture);

                            if (failAfter > 0 && row >= failAfter) SimulateDrop(svc);

                            if (row % 100 == 0) Console.WriteLine($"[SEND] row={row}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("[SIM] Prekid prenosa simuliran.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERR] Neočekivana greška: {ex.Message}");
                }
                finally
                {

                    var ch = svc.Proxy as IClientChannel;
                    if (ch != null && ch.State == CommunicationState.Opened)
                    {
                        svc.SafeCall(() => svc.Proxy.EndSession(vehicleId),
                                     onFault: reason => Console.WriteLine($"[ERR] EndSession fault: {reason}"),
                                     onOk: () => Console.WriteLine("[OK] EndSession"));
                    }
                    else
                    {
                        Console.WriteLine($"[INFO] Kanal je u stanju {ch?.State} – preskačem EndSession.");
                    }
                }
            }

            Console.WriteLine("Gotovo. Enter za izlaz.");
            Console.ReadLine();
        }

        

        static void TrySendRow(ChargingServiceClient svc, string rejectsPath, int row, string rawLine, string[] parts, string vehicleId, CultureInfo culture)
        {
            try
            {
                var s = ParseSample(parts, row, vehicleId, culture);
                svc.Proxy.PushSample(s);
            }
            catch (FaultException<FaultInfo> fe)
            {
                var reason = fe.Detail?.Reason ?? fe.Message;
                LogReject(rejectsPath, row, rawLine, $"SERVER_FAULT_TYPED: {reason}");
            }
            catch (FaultException fe)
            {
                LogReject(rejectsPath, row, rawLine, $"SERVER_FAULT_UNTYPED: {fe.Message}");
            }
            catch (CommunicationException ce)
            {
                LogReject(rejectsPath, row, rawLine, $"COMM_ERROR: {ce.Message}");
                throw; 
            }
            catch (TimeoutException te)
            {
                LogReject(rejectsPath, row, rawLine, $"TIMEOUT: {te.Message}");
                throw;
            }
            catch (Exception ex)
            {
                LogReject(rejectsPath, row, rawLine, ex.Message);
            }
        }

        static void SimulateDrop(ChargingServiceClient svc)
        {
            
            Console.WriteLine("[SIM] Kidam vezu (Abort) – simulacija prekida prenosa.");
            svc.Abort();
            throw new OperationCanceledException("Simulirani prekid prenosa.");
        }

        static string GuessDelimiter(string firstLine)
        {
            if (firstLine.Contains('\t')) return "\t";
            if (firstLine.Contains(';')) return ";";
            return ",";
        }
        static string[] SplitLine(string line, string delim) => line.Split(new[] { delim }, StringSplitOptions.None);
        static bool HeaderLooksLikeData(string[] cols)
            => DateTime.TryParse(cols[0], CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _);

        static ChargingSample ParseSample(string[] t, int row, string vehicleId, CultureInfo culture)
        {
            if (t.Length < 19) throw new FormatException("Premalo kolona u CSV redu.");
            return new ChargingSample
            {
                Timestamp = DateTime.Parse(t[0], culture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                VoltageRmsMin = ParseDouble(t[1], culture),
                VoltageRmsAvg = ParseDouble(t[2], culture),
                VoltageRmsMax = ParseDouble(t[3], culture),
                CurrentRmsMin = ParseDouble(t[4], culture),
                CurrentRmsAvg = ParseDouble(t[5], culture),
                CurrentRmsMax = ParseDouble(t[6], culture),
                RealPowerMin = ParseDouble(t[7], culture),
                RealPowerAvg = ParseDouble(t[8], culture),
                RealPowerMax = ParseDouble(t[9], culture),
                ReactivePowerMin = ParseDouble(t[10], culture),
                ReactivePowerAvg = ParseDouble(t[11], culture),
                ReactivePowerMax = ParseDouble(t[12], culture),
                ApparentPowerMin = ParseDouble(t[13], culture),
                ApparentPowerAvg = ParseDouble(t[14], culture),
                ApparentPowerMax = ParseDouble(t[15], culture),
                FrequencyMin = ParseDouble(t[16], culture),
                FrequencyAvg = ParseDouble(t[17], culture),
                FrequencyMax = ParseDouble(t[18], culture),
                RowIndex = row,
                VehicleId = vehicleId
            };
        }

        static double ParseDouble(string s, CultureInfo culture)
        {
            if (double.TryParse(s, NumberStyles.Any, culture, out double val))
                return val;
            throw new FormatException($"Nije broj: '{s}'");
        }

        static void LogReject(string path, int row, string line, string reason)
        {
            Console.WriteLine($"[WARN] row={row} - {reason}");
            File.AppendAllText(path, $"{row};{reason};{line}{Environment.NewLine}");
        }
    }

    sealed class ChargingServiceClient : IDisposable
    {
        public ChannelFactory<IChargingService> Factory { get; }
        public IChargingService Proxy { get; private set; }

        private bool _disposed;

        public ChargingServiceClient(string endpointConfigName)
        {
            Factory = new ChannelFactory<IChargingService>(endpointConfigName);
            Proxy = Factory.CreateChannel();
        }

        public void SafeCall(Action call, Action onOk = null, Action<string> onFault = null)
        {
            try
            {
                call();
                onOk?.Invoke();
            }
            catch (FaultException<FaultInfo> fe)
            {
                onFault?.Invoke(fe.Detail?.Reason ?? fe.Message);
            }
            catch (FaultException fe)
            {
                onFault?.Invoke(fe.Message);
            }
            catch (CommunicationObjectAbortedException coae)
            {
                onFault?.Invoke($"Channel aborted: {coae.Message}");
            }
            catch (CommunicationException ce)
            {
                onFault?.Invoke($"Communication error: {ce.Message}");
            }
            catch (TimeoutException te)
            {
                onFault?.Invoke($"Timeout: {te.Message}");
            }
        }

        public void Abort()
        {
            
            try { ((IClientChannel)Proxy)?.Abort(); } catch { }
            try { Factory?.Abort(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            
            try
            {
                var ch = Proxy as IClientChannel;
                if (ch != null)
                {
                    if (ch.State == CommunicationState.Faulted) ch.Abort();
                    else ch.Close();
                }
            }
            catch { try { (Proxy as IClientChannel)?.Abort(); } catch { } }

            try
            {
                if (Factory != null)
                {
                    if (Factory.State == CommunicationState.Faulted) Factory.Abort();
                    else Factory.Close();
                }
            }
            catch { try { Factory?.Abort(); } catch { } }

            Proxy = null;
        }
    }
}
