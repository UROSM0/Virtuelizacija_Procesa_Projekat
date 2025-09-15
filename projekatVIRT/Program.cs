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

            var factory = new ChannelFactory<IChargingService>("ChargingEndpoint");
            var proxy = factory.CreateChannel();

            string rejectsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rejects.csv");
            if (File.Exists(rejectsPath)) File.Delete(rejectsPath);

            try
            {
                proxy.StartSession(vehicleId);
                Console.WriteLine($"[OK] StartSession: {vehicleId}");

                var culture = CultureInfo.InvariantCulture;
                int row = 0;

                using (var fs = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sr = new StreamReader(fs))
                {
                    string firstLine = sr.ReadLine();
                    if (firstLine == null) throw new InvalidOperationException("CSV je prazan.");
                    string delim = GuessDelimiter(firstLine);
                    string[] cols = SplitLine(firstLine, delim);

                    // Ako header – preskoči
                    if (!HeaderLooksLikeData(cols))
                    {
                        Console.WriteLine("[INFO] Header prepoznat i preskočen.");
                    }
                    else
                    {
                        row++;
                        try
                        {
                            var s0 = ParseSample(cols, row, vehicleId, culture);
                            proxy.PushSample(s0);
                            Console.WriteLine($"[SEND] row={row}");
                        }
                        catch (Exception ex)
                        {
                            LogReject(rejectsPath, row, firstLine, ex.Message);
                        }
                    }

                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        row++;
                        try
                        {
                            var parts = SplitLine(line, delim);
                            var s = ParseSample(parts, row, vehicleId, culture);
                            proxy.PushSample(s);
                            if (row % 100 == 0) Console.WriteLine($"[SEND] row={row}");
                        }
                        catch (Exception ex)
                        {
                            LogReject(rejectsPath, row, line, ex.Message);
                        }
                    }
                }

                proxy.EndSession(vehicleId);
                Console.WriteLine("[OK] EndSession");
            }
            finally
            {
                try { ((IClientChannel)proxy)?.Close(); } catch { ((IClientChannel)proxy)?.Abort(); }
                try { factory?.Close(); } catch { factory?.Abort(); }
            }

            Console.WriteLine("Gotovo. Enter za izlaz.");
            Console.ReadLine();
        }

        static string GuessDelimiter(string firstLine)
        {
            if (firstLine.Contains('\t')) return "\t";
            if (firstLine.Contains(';')) return ";";
            return ",";
        }

        static string[] SplitLine(string line, string delim)
            => line.Split(new[] { delim }, StringSplitOptions.None);

        static bool HeaderLooksLikeData(string[] cols)
        {
            return DateTime.TryParse(cols[0], CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _);
        }

        static ChargingSample ParseSample(string[] t, int row, string vehicleId, CultureInfo culture)
        {
            if (t.Length < 19)
                throw new FormatException("Premalo kolona u CSV redu.");

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
}
