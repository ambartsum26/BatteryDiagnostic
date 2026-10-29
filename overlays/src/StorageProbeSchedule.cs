namespace BatteryDiagnostic;
internal sealed class StorageProbeSchedule
{
    int next;
    internal IEnumerable<int> Order(int count)
    {
        if(count<=0)yield break;
        int first=next%count;
        for(int offset=0;offset<count;offset++)yield return (first+offset)%count;
    }
    internal void Attempted(int index,int count)
    {
        if(count>0)next=(index+1)%count;
    }
}
