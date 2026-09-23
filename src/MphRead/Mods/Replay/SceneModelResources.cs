using System.Collections.Generic;

namespace MphRead;

public partial class Scene
{
    // Replicas never borrow the mutable model left in Read's foreground cache.
    // One copy per asset per scene keeps the existing within-scene batching.
    private readonly Dictionary<(string Name, bool FirstHunt), Model> _replicaModels = new();
    internal IReadOnlyDictionary<(string Name, bool FirstHunt), Model> ReplicaModels => _replicaModels;

    internal Model OwnModel(Model asset)
    {
        if (!Services.IsReplica) return asset;
        var key = (asset.Name, asset.FirstHunt);
        if (!_replicaModels.TryGetValue(key, out Model? owned))
            _replicaModels.Add(key, owned = asset.CreateSceneCopy());
        return owned;
    }
    internal ModelInstance GetModelInstance(string name, bool firstHunt = false,
        MetaDir dir = MetaDir.Models, bool noCache = false)
    {
        var instance = Read.GetModelInstance(name, firstHunt, dir, noCache);
        if (Services.IsReplica) instance.SetModel(noCache ? instance.Model.CreateSceneCopy() : OwnModel(instance.Model));
        return instance;
    }
    internal ModelInstance GetRoomModelInstance(string name)
    {
        var instance = Read.GetRoomModelInstance(name);
        instance.SetModel(OwnModel(instance.Model));
        return instance;
    }
    internal Node OwnParticleNode(Particle particle)
    {
        if (!Services.IsReplica) return particle.Node;
        Model model = OwnModel(particle.Model);
        for (int i = 0; i < particle.Model.Nodes.Count; i++)
            if (ReferenceEquals(particle.Model.Nodes[i], particle.Node)) return model.Nodes[i];
        throw new System.IO.InvalidDataException("Particle node is not part of its model asset.");
    }
}
