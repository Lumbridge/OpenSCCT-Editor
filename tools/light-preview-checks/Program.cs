using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;

if(args.Length is <4 or >5)
{
 Console.Error.WriteLine("Usage: LightPreviewChecks <project.osmap> <report.json> <content> <light-id> [baseline.json]");
 return 2;
}
string folder=Path.GetFullPath(args[0]), output=Path.GetFullPath(args[1]);
EditorServices.Discover();
EditorServices.ContentRoot=Path.GetFullPath(args[2]);
EditorServices.Classes=ClassCatalog.FromContent(EditorServices.ContentRoot);
var project=ProjectStore.Load(folder);
var preview=new LightPreview();
var results=new List<object>();
var light=project.Actors.Single(a=>a.Id==args[3]);
var original=light.Props["LightBrightness"];
for(int run=0;run<3;run++)
{
 light.Props["LightBrightness"]=run==1?SvJsonNode.Integer(20):original;
 long allocated=GC.GetTotalAllocatedBytes(true);
 int[] generations=[GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2)];
 var clock=Stopwatch.StartNew();
 var passes=new List<object>();
 foreach(var pass in preview.Passes(project,folder,new HashSet<string>(),run==0?LightPreviewStart.Cold:LightPreviewStart.Lights,CancellationToken.None))
 {
  using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
  foreach(var atlas in pass.Atlases)
  {
   hash.AppendData(BitConverter.GetBytes(atlas.Width)); hash.AppendData(BitConverter.GetBytes(atlas.Height)); hash.AppendData(atlas.Rgba);
  }
  foreach(var mesh in pass.Meshes.OrderBy(m=>m.Key,StringComparer.Ordinal))
  {
   hash.AppendData(Encoding.UTF8.GetBytes(mesh.Key));
   foreach(var colours in mesh.Value.Colours.OrderBy(p=>p.Key))
   { hash.AppendData(BitConverter.GetBytes(colours.Key)); hash.AppendData(colours.Value); }
  }
  passes.Add(new { pass.Name,pass.Final,pass.Stats.Charts,pass.Stats.Texels,meshCount=pass.Meshes.Count,sha256=Convert.ToHexString(hash.GetHashAndReset()) });
 }
 clock.Stop();
 var process=Process.GetCurrentProcess();
 var result=new {run,elapsedMs=clock.ElapsedMilliseconds,allocatedBytes=GC.GetTotalAllocatedBytes(true)-allocated,
  gen0=GC.CollectionCount(0)-generations[0],gen1=GC.CollectionCount(1)-generations[1],gen2=GC.CollectionCount(2)-generations[2],
  heapBytes=GC.GetTotalMemory(false),committedBytes=GC.GetGCMemoryInfo().TotalCommittedBytes,privateBytes=process.PrivateMemorySize64,passes};
 results.Add(result); Console.WriteLine(JsonSerializer.Serialize(result));
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output,JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
var actual=JsonNode.Parse(File.ReadAllText(output))!.AsArray();
string FinalHash(JsonNode? run)=>run!["passes"]!.AsArray().Last()!["sha256"]!.GetValue<string>();
if(FinalHash(actual[0])!=FinalHash(actual[2]) || FinalHash(actual[0])==FinalHash(actual[1]))
 throw new InvalidOperationException("The chosen light must change the bake, and restoring it must restore every final lighting byte.");
if(args.Length==5)
{
 var expected=JsonNode.Parse(File.ReadAllText(args[4]))!.AsArray();
 if(expected.Count!=actual.Count || expected.Where((run,index)=>!JsonNode.DeepEquals(run!["passes"],actual[index]!["passes"])).Any())
  throw new InvalidOperationException("Lightmap/mesh-colour fingerprints differ from the baseline.");
 Console.WriteLine("PASS all cold, changed-light and restored-light pass fingerprints match the baseline");
}
return 0;
