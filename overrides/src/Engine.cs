using System.Runtime.InteropServices;
namespace BatteryDiagnostic;

internal sealed class Reading
{
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public int Slot { get; set; }
    public string? BatteryModel { get; set; }
    public string? Manufacturer { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string Identity { get; set; } = "";
    public bool Relative { get; set; }
    public double? Design { get; set; }
    public double? Full { get; set; }
    public double? Capacity { get; set; }
    public double? Voltage { get; set; }
    public double? Rate { get; set; }
    public double? Temperature { get; set; }
    public uint? Cycles { get; set; }
    public uint? State { get; set; }
    public Observation? Observation { get; set; }
    public List<string> Errors { get; set; } = [];
    public double? Percent => Full > 0 && Capacity >= 0 ? 100 * Capacity / Full : null;
}
internal enum CapacityGrade { NoData, Unclassified, Critical, Medium, Perfect }
internal sealed record Assessment(double? Retained, CapacityGrade Grade, List<string> Signals, List<string> Notes);
internal sealed record ChargeView(double? Percent, string SourceKey, bool? Online, bool? Charging, string StateKey);
internal static class Engine
{
    internal static string GradeLabelKey(CapacityGrade grade) => grade switch
    {
        CapacityGrade.Critical=>"gradeCritical",
        CapacityGrade.Medium=>"gradeMedium",
        CapacityGrade.Perfect=>"gradePerfect",
        CapacityGrade.Unclassified=>"unclassified",
        _=>"unknown"
    };

    internal static CapacityGrade ClassifyCapacity(double? retained)
    {
        if(!retained.HasValue || !double.IsFinite(retained.Value))return CapacityGrade.NoData;
        double value=retained.Value;
        if(value is >=1 and <40)return CapacityGrade.Critical;
        if(value is >=40 and <80)return CapacityGrade.Medium;
        if(value is >=80 and <=100)return CapacityGrade.Perfect;
        return CapacityGrade.Unclassified;
    }

    internal static Assessment Evaluate(Reading r, UiLanguage language)
    {
        string T(string key) => L.Get(language, key);
        bool valid = r.Design is {} design && double.IsFinite(design) && design>0 && r.Full is {} full && double.IsFinite(full) && full>0 && full<=design*1.2;
        double? retained = valid ? 100 * r.Full!.Value / r.Design!.Value : null;
        var signals = new List<string>();
        var notes = new List<string>();
        if (!valid && (r.Full.HasValue || r.Design.HasValue)) notes.Add(T("invalidCapacity"));
        if (r.Percent > 100.5) notes.Add(T("capacityMismatch"));
        if (valid && r.Full > r.Design) notes.Add(T("aboveDesign"));
        if ((r.State.GetValueOrDefault() & 8) != 0) signals.Add(T("criticalSignal"));
        if ((r.State.GetValueOrDefault() & 6) == 6) notes.Add(T("stateConflict"));
        if (r.Temperature >= 50) signals.Add(T("hot"));
        if (r.Voltage == 0) notes.Add(T("zeroVoltage"));
        notes.AddRange(r.Errors.Select(e => L.F(language, "sourceError", e)));
        return new Assessment(retained, ClassifyCapacity(retained), signals, notes);
    }

    internal static ChargeView Charge(Reading? r, BatteryData? data)
    {
        var p = data?.Power;
        bool usableWindows = p is { Flags: not 255 } known && (known.Flags & 128) == 0;
        bool single = data?.Batteries.Count <= 1;
        bool? online = p?.AC == 1 ? true : p?.AC == 0 ? false
            : r?.State is { } st && (st & 1) != 0 ? true : null;
        bool? charging = r?.State is { } state ? (state & 4) != 0
            : single && usableWindows ? (p!.Value.Flags & 8) != 0 : null;
        double? percent = null;
        string source = "sourceUnknown";
        if (single && usableWindows && p!.Value.Percent <= 100)
        {
            percent = p.Value.Percent; source = "sourceWindows";
        }
        else if (r?.Percent is >= 0 and <= 100)
        {
            percent = r.Percent; source = "sourceBattery";
        }
        bool stateAvailable = r?.State.HasValue == true || single && usableWindows;
        string stateKey = !stateAvailable ? "unknown" : (r?.State.GetValueOrDefault() & 8) != 0 && r != null ? "critical"
            : (r?.State.GetValueOrDefault() & 6) == 6 ? "conflict"
            : charging == true ? "charging"
            : online == true ? "connectedIdle"
            : (r?.State.GetValueOrDefault() & 2) != 0 && r != null || online == false ? "discharging"
            : r?.State.HasValue == true ? "idle" : "unknown";
        return new ChargeView(percent, source, online, charging, stateKey);
    }

    internal static void SelfTest()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception("Self-test failed: " + name); }
        L.Validate();SessionMonitor.SelfTest();ReportWriter.SelfTest();
        var r = new Reading { Design = 62160, Full = 56044, Capacity = 55178, State = 5, Voltage = 12555, Rate = 6403 };
        var d = new BatteryData([r], new Native.Power { AC = 1, Flags = 8, Percent = 98 }, DateTimeOffset.Now, "TEST");
        Check(Math.Abs(Evaluate(r, UiLanguage.French).Retained!.Value - 90.1609) < .001, "retained capacity formula");
        Check(ClassifyCapacity(1)==CapacityGrade.Critical && ClassifyCapacity(39)==CapacityGrade.Critical && ClassifyCapacity(39.99)==CapacityGrade.Critical, "critical grade boundaries");
        Check(ClassifyCapacity(40)==CapacityGrade.Medium && ClassifyCapacity(79)==CapacityGrade.Medium && ClassifyCapacity(79.99)==CapacityGrade.Medium, "medium grade boundaries");
        Check(ClassifyCapacity(80)==CapacityGrade.Perfect && ClassifyCapacity(100)==CapacityGrade.Perfect, "perfect grade boundaries");
        Check(ClassifyCapacity(0)==CapacityGrade.Unclassified && ClassifyCapacity(100.1)==CapacityGrade.Unclassified, "unclassified ranges stay explicit");
        Check(ClassifyCapacity(null)==CapacityGrade.NoData, "missing condition remains no data");
        Check(L.Get(UiLanguage.French,GradeLabelKey(CapacityGrade.Critical))=="Critique" && L.Get(UiLanguage.French,GradeLabelKey(CapacityGrade.Medium))=="Moyen" && L.Get(UiLanguage.French,GradeLabelKey(CapacityGrade.Perfect))=="Parfait", "French capacity grades");
        Check(L.Get(UiLanguage.English,GradeLabelKey(CapacityGrade.Critical))=="Critical" && L.Get(UiLanguage.English,GradeLabelKey(CapacityGrade.Medium))=="Moderate" && L.Get(UiLanguage.English,GradeLabelKey(CapacityGrade.Perfect))=="Excellent", "English capacity grades");
        Check(L.Get(UiLanguage.Russian,GradeLabelKey(CapacityGrade.Critical))=="Критическое" && L.Get(UiLanguage.Russian,GradeLabelKey(CapacityGrade.Medium))=="Среднее" && L.Get(UiLanguage.Russian,GradeLabelKey(CapacityGrade.Perfect))=="Отличное", "Russian capacity grades");
        Check(L.Get(UiLanguage.French,"healthTip").Contains("1–39 %") && L.Get(UiLanguage.French,"healthTip").Contains("40–79 %") && L.Get(UiLanguage.French,"healthTip").Contains("80–100 %"), "French scale explanation");
        Check(L.Get(UiLanguage.English,"healthTip").Contains("1–39%") && L.Get(UiLanguage.English,"healthTip").Contains("40–79%") && L.Get(UiLanguage.English,"healthTip").Contains("80–100%"), "English scale explanation");
        Check(L.Get(UiLanguage.Russian,"healthTip").Contains("1–39%") && L.Get(UiLanguage.Russian,"healthTip").Contains("40–79%") && L.Get(UiLanguage.Russian,"healthTip").Contains("80–100%"), "Russian scale explanation");
        Check(Charge(r, d).Percent == 98 && Charge(r, d).SourceKey == "sourceWindows", "Windows charge and source");
        r.Capacity = 62160; d = d with { Power = new Native.Power { AC = 1, Flags = 0, Percent = 100 } }; r.State = 1;
        Check(Charge(r, d).Percent == 100 && Charge(r, d).StateKey == "connectedIdle", "observed capacity mismatch retains Windows 100");
        Check(Evaluate(r, UiLanguage.English).Signals.Count == 0 && Evaluate(r, UiLanguage.English).Notes.Count > 0, "data mismatch is not a defect signal");
        r.Capacity = 28022; d = d with { Batteries = [r, new Reading { Slot = 1 }] };
        Check(Charge(r, d).Percent == 50 && Charge(r, d).SourceKey == "sourceBattery", "multi-battery must not reuse aggregate Windows percentage");
        r.Capacity = 62160;
        Check(Charge(r, d).Percent == null, "inconsistent multi-battery charge remains unknown");
        r.Capacity = 28022; d = d with { Batteries = [r], Power = new Native.Power { AC = 255, Flags = 255, Percent = 255 } };
        Check(Charge(r, d).Percent == 50, "unknown Windows charge falls back to valid battery data");
        Check(Evaluate(new Reading {Design=double.NaN,Full=50000},UiLanguage.French).Retained==null,"NaN rejected");
        Check(Evaluate(new Reading {Design=60000,Full=-1},UiLanguage.French).Retained==null,"negative capacity rejected");
        var empty = new Reading();
        Check(Evaluate(empty, UiLanguage.French).Retained == null && Charge(empty, null).Percent == null, "missing data");
        var noBattery = new BatteryData([], new Native.Power { AC = 1, Flags = 128, Percent = 100 }, DateTimeOffset.Now, "TEST");
        Check(Charge(null, noBattery).Percent == null, "no-battery flag must not fabricate charge");
        r.Full = 0; Check(Evaluate(r, UiLanguage.English).Retained == null, "zero full capacity");
        r.Full = 80000; Check(Evaluate(r, UiLanguage.English).Retained == null, "unreliable full capacity");
        r.Full = 56044; r.State = 8;
        Check(Evaluate(r, UiLanguage.English).Signals.Count == 1, "critical signal preserved");
        r.State = 6;
        Check(Evaluate(r, UiLanguage.English).Notes.Count > 0 && Charge(r, d).StateKey == "conflict", "charge flag conflict");
        Check(r.Cycles == null && r.Temperature == null, "unsupported sensors remain missing");
        Check(Native.DecodeTemperature(2981) is { } temp && Math.Abs(temp-(298.1-273.15))<0.0001, "tenths-Kelvin temperature conversion");
        Check(Native.DecodeTemperature(0)==null && Native.DecodeTemperature(uint.MaxValue)==null && Native.DecodeTemperature(5000)==null, "invalid temperature remains missing");
        var encodedName=System.Text.Encoding.Unicode.GetBytes("Pack A, Rev \"2\"\0");
        Check(Native.DecodeUnicodeText(encodedName,encodedName.Length)=="Pack A, Rev \"2\"" && Native.DecodeUnicodeText(encodedName,1)==null && Native.DecodeUnicodeText([],0)==null, "battery name decoding and validation");
        Check(IndicatorWidget.HealthDotCount(CapacityGrade.Critical)==1 && IndicatorWidget.HealthDotCount(CapacityGrade.Medium)==2 && IndicatorWidget.HealthDotCount(CapacityGrade.Perfect)==3 && IndicatorWidget.HealthDotCount(CapacityGrade.NoData)==0 && IndicatorWidget.HealthDotCount(CapacityGrade.Unclassified)==0, "health indicator dot counts");
        Check(IndicatorWidget.HealthColor(CapacityGrade.Critical)==Theme.Danger && IndicatorWidget.HealthColor(CapacityGrade.Medium)==Theme.Warning && IndicatorWidget.HealthColor(CapacityGrade.Perfect)==Theme.Accent, "health indicator colors");
        Check(IndicatorWidget.ChargeDotCount(null)==0 && IndicatorWidget.ChargeDotCount(0)==0 && IndicatorWidget.ChargeDotCount(1)==1 && IndicatorWidget.ChargeDotCount(25)==1 && IndicatorWidget.ChargeDotCount(26)==2 && IndicatorWidget.ChargeDotCount(100)==4, "charge indicator dot counts");
        Check(IndicatorWidget.ChargeColor(null)==Theme.Muted && IndicatorWidget.ChargeColor(15)==Theme.Warning && IndicatorWidget.ChargeColor(16)==Theme.Accent, "charge indicator colors");
        Check(new[]{UiLanguage.French,UiLanguage.English,UiLanguage.Russian}.All(x=>!string.IsNullOrWhiteSpace(L.Get(x,"batteryModel"))), "battery model localized in all languages");
        Check(Enum.GetValues<UiLanguage>().All(x=>L.F(x,"healthTip","26.11 Wh","68.00 Wh",L.Percent(x,38.4)).Split('\n').Length==5), "health explanation is arranged on five lines in every language");
        Check(Marshal.SizeOf<Native.Power>() == 12, "native structure");
        Check(L.Get(UiLanguage.English, "charge") == "Charge status", "English translation");
    }
}
