namespace XivCloth.Core;

/// <summary>Opaque reference-only model identity. Deliberately sealed and not
/// a record; arbitrary source.Equals implementations cannot forge equality.
/// An adapter owns one token per exact immutable calibration and observation
/// continuity. Recalibration or adapter reset creates a new token, even when
/// numeric actor fields and calibrated dimensions happen to match.</summary>
public sealed class FootProxyModelBinding
{
    private readonly object source;
    internal FootProxyIdentity Identity { get; }
    private FootProxyModelBinding(FootProxyIdentity identity,object source)
    {Identity=identity;this.source=source;}
    public static bool TryCreate(FootProxyIdentity identity,object? immutableModelSource,out FootProxyModelBinding? binding)
    {
        binding=null;
        if(!identity.Valid||identity.ModelBinding!=null||immutableModelSource==null)return false;
        binding=new(identity,immutableModelSource);return true;
    }
    internal bool Matches(object? modelSource)=>ReferenceEquals(source,modelSource);
}
