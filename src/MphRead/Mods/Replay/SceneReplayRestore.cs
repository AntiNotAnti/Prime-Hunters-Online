using System;
using System.IO;
using System.Linq;
using MphRead.Effects;
using MphRead.Entities;

namespace MphRead;

public partial class Scene
{
    internal void FinishReplayWorldRestore()
    {
        if (!Services.IsReplica) throw new InvalidOperationException("World restoration requires a private replica.");
        var membership = _entities.ToArray();
        _entities.Clear(); _entityMap.Clear();
        foreach (EntityType type in _entityNodesByType.Keys.ToArray()) _entityNodesByType[type] = null;
        foreach (EntityBase entity in membership)
        {
            InsertEntity(entity);
            InitEntity(entity); // resource binding only; never Initialize/Process
            if (entity is BeamProjectileEntity beam) beam.ReplayBindResources();
            else if (entity is BombEntity bomb) bomb.ReplayBindResources();
        }
        foreach (EffectElementEntry element in _activeElements.Concat(_inactiveElements))
        {
            element.Random = Random;
            element.TextureBindingIds.Clear(); element.ParticleDefinitions.Clear(); element.Nodes.Clear();
            element.Definition = null; element.Model = null!;
            if (element.ElementName.Length == 0) continue;
            var effect = Read.GetEffect(element.EffectId) ?? Read.LoadEffect(element.EffectId, persistent: true);
            if ((uint)element.DefinitionIndex >= (uint)effect.Elements.Count
                || effect.Elements[element.DefinitionIndex].Name != element.ElementName)
                throw new InvalidDataException("Missing historical effect asset.");
            var definition = effect.Elements[element.DefinitionIndex];
            element.Definition = definition; element.Funcs = definition.Funcs; element.Actions = definition.Actions;
            element.ParticleDefinitions.AddRange(definition.Particles);
            foreach (Particle particle in definition.Particles)
            {
                Model model = OwnModel(particle.Model);
                element.Model ??= model;
                LoadModel(model);
                element.Nodes.Add(OwnParticleNode(particle));
                Material material = model.Materials[particle.MaterialId];
                element.TextureBindingIds.Add(BindGetTexture(model, material.TextureId, material.PaletteId, 0));
            }
            foreach (EffectParticle particle in element.Particles) particle.Random = Random;
        }
        foreach (EffectParticle particle in _inactiveParticles) particle.Random = Random;
    }
}
