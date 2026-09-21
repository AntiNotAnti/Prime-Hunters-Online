using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MphRead.Mods.MapGen;

namespace MphRead.Mods.MapEditor
{
    public sealed record MapAuditResult(bool Passed, int ExitCode, IReadOnlyList<string> Lines);

    public static class MapAuditRunner
    {
        public static async Task<MapAuditResult> Run(MapProject project,CancellationToken cancellation)
        {
            string directory=Path.Combine(Path.GetTempPath(),"project-prime-map-audit-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var definition=project.ToDefinition();definition.Name="AUDIT "+Guid.NewGuid().ToString("N");
                MapProjectExport.Save(definition,Path.Combine(directory,"audit.json"));
                var start=new ProcessStartInfo(Environment.ProcessPath??throw new IOException("Executable path is unavailable."))
                {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=AppContext.BaseDirectory};
                if(Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ProjectPrime.dll"));
                string mode=definition.Capabilities?.SupportedModes.FirstOrDefault()??"Battle";
                int players=definition.Capabilities?.MaxPlayers??8;
                foreach(string arg in new[]{"-mapdir",directory,"-maptest",definition.Name,"-players",players.ToString(),"-mode",mode,"-seconds","22","-bots","-noupdate"})start.ArgumentList.Add(arg);
                using var process=Process.Start(start)??throw new IOException("Could not start the map audit.");
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromMinutes(4));
                var lines=new List<string>();
                async Task Read(StreamReader reader)
                {
                    while(await reader.ReadLineAsync(timeout.Token) is {} line)
                    {lock(lines){if(lines.Count<2000)lines.Add(line.Length>4096?line[..4096]:line);}}
                }
                try
                {
                    await Task.WhenAll(Read(process.StandardOutput),Read(process.StandardError),process.WaitForExitAsync(timeout.Token));
                    return new(process.ExitCode==0,process.ExitCode,lines);
                }
                finally{if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}}
            }
            finally{Directory.Delete(directory,true);}
        }
    }
}
