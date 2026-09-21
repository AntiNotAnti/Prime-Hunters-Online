using System;
using MphRead.Mods.MapGen;

namespace MphRead.Mods.MapEditor
{
    public static class MapTemplates
    {
        public static MapProject Create(string name, bool enclosed = false, bool teams = false)
        {
            MapValidator.RequireRuntimeName(name);
            var d = new MapDefinition { FormatVersion=2, MapId=Guid.NewGuid(), Name=name.ToUpperInvariant(), InGameName=name,
                Version="1.0.0", FogEnabled=false, KillHeight=-20, Preview=new() { Position=new[]{25f,22,30}, Target=new[]{0f,0,0} } };
            d.Materials.Add(new() { Id=Guid.NewGuid(), Name="Arena", SourceMaterial=2, TexScale=16 });
            d.Capabilities=new(){SupportedModes=teams?new(){"BattleTeams","SurvivalTeams"}:new(){"Battle","Survival"}};
            d.Geometry.Add(new MapBox { Label="Floor", Transform=new() { Position=new[]{0f,-.5f,0}, Scale=new[]{32f,1,32} } });
            if (enclosed)
            {
                d.Geometry.Add(new MapBox { Label="Ceiling", Transform=new() { Position=new[]{0f,10.5f,0}, Scale=new[]{32f,1,32} } });
                for(int i=0;i<4;i++) d.Geometry.Add(new MapBox { Label="Wall "+(i+1), Transform=new()
                { Position=new[]{i<2?(i==0?-16f:16f):0,5,i>=2?(i==2?-16f:16f):0}, Scale=i<2?new[]{1f,10,32}:new[]{32f,10,1} } });
                for(int i=0;i<8;i++)
                {
                    float angle=i*MathF.PI/4;
                    d.Spawns.Add(new() { Id=Guid.NewGuid(), Label="Spawn "+(i+1), Position=new[]{MathF.Sin(angle)*9,.1f,MathF.Cos(angle)*9}, Yaw=i*45+180, Team=teams?i%2:-1 });
                }
                d.Items.Add(new() { Id=Guid.NewGuid(), Type="HealthMedium", Position=new[]{0f,.1f,0} });
            }
            else d.Spawns.Add(new() { Id=Guid.NewGuid(), Position=new[]{0f,.1f,0} });
            return new(d);
        }
    }
}
