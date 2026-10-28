using System.Collections;
using System.Reflection;
using DiskInfoToolkit.Devices;
using DiskInfoToolkit.Smart;

namespace BatteryDiagnostic;

// Pure merge fixtures: no source enumeration, drivers, DLL loads or physical disks.
internal static class AuditHealthTests
{
    internal static void Run()
    {
        var failures = new List<string>();
        void Check(bool condition, string reason) { if (!condition) failures.Add(reason); }
        void Case(string name, Action run)
        {
            try { run(); }
            catch (Exception ex) { failures.Add(name + ": fixture failed: " + (ex.InnerException ?? ex).Message); }
        }
        StorageDisk Toolkit(StorageDisk disk, StorageDevice item) =>
            (StorageDisk)typeof(PerformanceStorage).GetMethod("MergeToolkit", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [disk, item])!;
        StorageDisk Cdi(StorageDisk disk, int life, StorageSmartAttribute[] attributes)
        {
            var sampleType = typeof(LibCdiStorage).GetNestedType("Sample", BindingFlags.NonPublic)!;
            var sample = Activator.CreateInstance(sampleType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, [disk.Index!.Value, disk.Model, 1, null, life, null, null, null, null, attributes], null)!;
            var samples = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(sampleType))!;
            samples.Add(sample);
            var disks = new List<StorageDisk> { disk };
            typeof(LibCdiStorage).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [disks, samples, new List<string>()]);
            return disks.Single();
        }

        Case("Toolkit prediction", () =>
        {
            foreach (bool? previous in new bool?[] { null, false })
            {
                var disk = Toolkit(new StorageDisk { HealthCode = "ProviderHealthy", FailurePredicted = previous },
                    new StorageDevice { PredictsFailure = true });
                var result = DiskAssessmentEngine.Evaluate(disk);
                Check(disk.FailurePredicted == true, "Toolkit positive prediction overrides absent/negative prediction: " + previous);
                Check(result.StateKey == "predicted" && result.Color == Theme.Danger,
                    "Toolkit positive failure prediction must display danger: " + previous);
            }
        });
        Case("Toolkit HDD sectors", () =>
        {
            foreach (byte id in new byte[] { 0x05, 0xC5, 0xC6 })
            {
                var item = new StorageDevice();
                item.SmartAttributes.Add(new SmartAttributeEntry { ID = id, CurrentValue = 100, WorstValue = 100, ThresholdValue = 10, RawValue = 1 });
                var disk = Toolkit(new StorageDisk { Model = "Fixture HDD", MediaType = "HDD", HealthCode = "ProviderHealthy" }, item);
                var result = DiskAssessmentEngine.Evaluate(disk);
                Check(disk.AtaSectorWarning == true, "Toolkit sector fixture decoded: " + id);
                Check(result.StateKey == "sectorWarning" && result.Color == Theme.Warning,
                    "Toolkit HDD sector caution must survive healthy provider: " + id);
            }
        });
        Case("LibCdi conflicting SC311", () =>
        {
            var disk = Cdi(new StorageDisk { Index = 0, Model = "SK hynix SC311 SATA 256GB", MediaType = "SSD", HealthCode = "ProviderHealthy" },
                50, [new StorageSmartAttribute(0xE9, 2, 1, null, 99, false)]);
            var result = DiskAssessmentEngine.Evaluate(disk);
            Check(disk.RemainingLifePercent is null, "LibCdi SC311 contradiction withholds percentage");
            Check(result.StateKey == "dataInvalid" && result.Color == Theme.Muted,
                "LibCdi contradictory lifetime must not display healthy/green");
        });
        Case("LibCdi independent lifetime conflict", () =>
        {
            var disk = Cdi(new StorageDisk { Index = 0, Model = "Samsung SSD 850 EVO 250GB", MediaType = "SSD",
                HealthCode = "ProviderHealthy", RemainingLifePercent = 95, RemainingLifeSource = "AtaSmart:SamsungSataConsumerB1" },
                20, [new StorageSmartAttribute(0xB1, 20, 20, 0, 80, false)]);
            var result = DiskAssessmentEngine.Evaluate(disk);
            Check(disk.RemainingLifePercent is null && result.Remaining is null,
                "Independent lifetime disagreement must withhold the old optimistic 95 percent");
            Check(result.StateKey == "dataInvalid" && result.Color == Theme.Muted,
                "Independent lifetime disagreement must be a visible invalid-data condition");
        });
        if (failures.Count > 0) throw new Exception("Health audit regression failures (" + failures.Count + "): " + string.Join("; ", failures));
    }
}
