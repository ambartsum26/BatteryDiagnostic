namespace BatteryDiagnostic;

// Passive checks of controller reports, not an electrical load test or OEM self-test.
internal sealed record Observation(int Samples,double Seconds,string[] Findings,int ValidSamples);
internal sealed class SessionMonitor
{
    sealed class Track
    {
        internal DateTimeOffset Start,Last;
        internal double? Percent,Full;
        internal bool? Online,Charging;
        internal int Samples,ValidSamples,Conflicts;
        internal readonly Dictionary<string,DateTimeOffset> Findings=[];
    }
    readonly Dictionary<string,Track> tracks=[];
    internal void Gap()=>tracks.Clear();
    internal void Add(BatteryData data)
    {
        var present=new HashSet<string>();
        foreach(var r in data.Batteries)
        {
            string key=r.Identity.Length>0?r.Identity:"slot:"+r.Slot;
            present.Add(key);
            var charge=Engine.Charge(r,data);
            if(!tracks.TryGetValue(key,out var t)||data.Time<=t.Last||(data.Time-t.Last).TotalSeconds>15)
                tracks[key]=t=new Track {Start=data.Time};
            double elapsed=(data.Time-t.Last).TotalSeconds;
            bool comparable=t.Samples>0 && elapsed<=10 && t.Online==charge.Online && t.Charging==charge.Charging;
            void Note(string code)=>t.Findings[code]=data.Time;
            if(comparable && charge.Percent.HasValue && t.Percent.HasValue && Math.Abs(charge.Percent.Value-t.Percent.Value)>=5)Note("jumpCharge");
            if(comparable && r.Full>0 && t.Full>0 && Math.Abs(r.Full.Value-t.Full.Value)/t.Full.Value>.05)Note("jumpFull");
            // Windows and battery values can share a controller: agreement is not independent validation.
            if(data.Batteries.Count==1 && charge.SourceKey=="sourceWindows" && r.Percent is >=0 and <=100 && Math.Abs(r.Percent.Value-charge.Percent!.Value)>5)Note("percentDisagreement");
            bool conflict=!r.Relative && r.Rate.HasValue && ((r.State.GetValueOrDefault()&6)==4 && r.Rate< -100 || (r.State.GetValueOrDefault()&6)==2 && r.Rate>100);
            t.Conflicts=conflict && t.Samples>0 && elapsed<=10?t.Conflicts+1:conflict?1:0;
            if(t.Conflicts>=3 && (data.Time-t.Start).TotalSeconds>=4)Note("rateConflict");
            if(Engine.Evaluate(r,UiLanguage.English).Retained.HasValue && charge.Percent.HasValue && r.Errors.Count==0)t.ValidSamples++;
            t.Samples++;t.Last=data.Time;t.Percent=charge.Percent;t.Full=r.Full;t.Online=charge.Online;t.Charging=charge.Charging;
            r.Observation=new Observation(t.Samples,(data.Time-t.Start).TotalSeconds,t.Findings.Keys.ToArray(),t.ValidSamples);
        }
        foreach(var key in tracks.Keys.Where(k=>!present.Contains(k)).ToArray())tracks.Remove(key);
    }
    internal static void SelfTest()
    {
        void Check(bool ok,string name){if(!ok)throw new Exception("Session check failed: "+name);}
        var monitor=new SessionMonitor();var time=DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        BatteryData Sample(int seconds,byte percent,double full=50000,double rate=-1000)=>new([new Reading {Identity="test",Design=60000,Full=full,Capacity=25000,State=2,Rate=rate}],new Native.Power {AC=0,Flags=0,Percent=percent},time.AddSeconds(seconds),"TEST");
        var a=Sample(0,50);monitor.Add(a);Check(a.Batteries[0].Observation!.Samples==1,"first sample is not a stability test");
        a=Sample(2,50);monitor.Add(a);Check(a.Batteries[0].Observation!.Findings.Length==0,"consistent readings");
        a=Sample(4,40,45000);monitor.Add(a);Check(a.Batteries[0].Observation!.Findings.Contains("jumpCharge")&&a.Batteries[0].Observation!.Findings.Contains("jumpFull"),"explainable jumps");
                monitor.Gap();monitor.Add(Sample(0,50));a=Sample(2,10) with {Power=new Native.Power {AC=1,Flags=0,Percent=10}};monitor.Add(a);
        Check(!a.Batteries[0].Observation!.Findings.Contains("jumpCharge"),"power transitions excluded");
        monitor.Gap();for(int second=0;second<=32;second+=2){a=Sample(second,50);monitor.Add(a);}
        Check(a.Batteries[0].Observation!.ValidSamples==17&&a.Batteries[0].Observation!.Seconds==32,"usable observation duration and count");
                monitor.Gap();monitor.Add(Sample(0,50));for(int second=2;second<=130;second+=2){a=Sample(second,40);monitor.Add(a);}
        Check(a.Batteries[0].Observation!.Findings.Contains("jumpCharge"),"finding retained for the observation");
        monitor.Gap();a=Sample(6,10);monitor.Add(a);Check(!a.Batteries[0].Observation!.Findings.Contains("jumpCharge"),"no comparison over a failed read");
        monitor.Gap();foreach(int second in new[]{0,2,4}){a=Sample(second,50,50000,1000);monitor.Add(a);}
        Check(a.Batteries[0].Observation!.Findings.Contains("rateConflict"),"persistent rate contradiction");
        a=Sample(40,30);monitor.Add(a);Check(a.Batteries[0].Observation!.Samples==1&&!a.Batteries[0].Observation!.Findings.Contains("jumpCharge"),"sleep gap resets baseline");
                monitor.Gap();var missing=a with {Batteries=[new Reading()]};monitor.Add(missing);
        Check(missing.Batteries[0].Observation!.ValidSamples==0,"missing data is never valid evidence");
        monitor.Add(a with {Batteries=[]});a=Sample(42,90);monitor.Add(a);Check(a.Batteries[0].Observation!.Samples==1,"removed battery not compared");
    }
}
