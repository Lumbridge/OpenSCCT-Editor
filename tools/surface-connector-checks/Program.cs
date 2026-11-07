using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Scripting;
int checks=0;
void Check(bool ok,string message) { if (!ok) throw new Exception(message);checks++; }
Brush Room(string name,double low,double high) => new(name) { Oper=CsgOper.Subtract, Shape=Box.Shape(new(1024,1024,high-low)), Location=new(0,0,(low+high)/2) };
foreach (int sign in new[] {-1,1}) foreach (int yaw in new[] {0,8192})
{
 var p=MapProject.CreateDefault("Connections","Vertical connectors");
 var source=Room("Room",0,384);p.Brushes=[source,Room("Above",448,832),Room("Below",-448,-64)];
 foreach (var b in p.Brushes) b.Rotation=new(0,yaw,0);
 int index=source.Shape.Faces.FindIndex(f=>f.Normal().Z*sign>.95);
 var face=new FaceRef(source.Id,index);var point=new Vec3(48,32,sign>0?384:0);
 var a=WallPieces.Anchor(p,face,point)!.Value;
 Check(a.Vertical && a.Surface==(sign>0?"ceiling":"floor"),"Surface orientation chooses the connector direction");
 Check((a.Point-point).Length<.00001,"Connector stays at the hovered surface point");
 Check(WallPieces.KindsAt(a).SequenceEqual(WallPieces.VerticalKinds),"Floor/ceiling menu is contextual");
 var before=new WorldGeometry(p,null);var middle=a.Point+a.Into*32;
 Check(before.IsSolid(middle),"Fixture starts with a solid slab");
 Check(Math.Abs(WallPieces.WallDepth(before,a,0)-64)<.01,"Detects the actual slab thickness");
 var added=WallPieces.Build(p,a,"hatch");var command=new AddObjectsCommand("Add hatch",added);command.Do(p);
 Check(!new WorldGeometry(p,null).IsSolid(middle),"Hatch reaches the adjoining room through the slab");
 Check(new WorldGeometry(p,null).IsSolid(middle+new Vec3(300,0,0)),"Floor/ceiling outside opening is retained");
 command.Undo(p);Check(new WorldGeometry(p,null).IsSolid(middle),"Undo restores the whole slab");
 command.Do(p);Check(!new WorldGeometry(p,null).IsSolid(middle),"Redo restores the connector");
 var single=MapProject.CreateDefault("NewRoom","Connected room");single.Brushes=[source.Clone()];
 var room=WallPieces.Build(single,a,"room");new AddObjectsCommand("Add connected room",room).Do(single);
 Check(room.Count==2 && !new WorldGeometry(single,null).IsSolid(a.Point+a.Into*200),"Adds a room above/below with a connecting opening");
 Check(!new WorldGeometry(single,null).IsSolid(a.Point+a.Into*16),"New room connects without a sealed slab");
 source.Editor.Locked=true;Check(WallPieces.Anchor(p,face,point) is null,"Locked surfaces cannot build connectors");source.Editor.Locked=false;
 source.Editor.Hidden=true;Check(WallPieces.Anchor(p,face,point) is null,"Hidden surfaces cannot build connectors");source.Editor.Hidden=false;
 if (args.Length>0 && sign>0 && yaw==0) { p.Workspace.CameraLocation=new(-400,-400,220);p.Workspace.CameraRotation=new(-3500,8192,0);ProjectStore.Save(p,Path.GetFullPath(args[0])); }
}
foreach (int sign in new[] {-1,1})
{
 var p=MapProject.CreateDefault("Slab","Editable slab");var slab=Box.Make("Slab",new(512,512,64),CsgOper.Add);slab.Location=new(0,0,416);
 p.Brushes=[Room("Room",0,1000),slab];int index=slab.Shape.Faces.FindIndex(f=>f.Normal().Z*sign>.95);
 var anchor=WallPieces.Anchor(p,new(slab.Id,index),new Vec3(0,0,416+sign*32))!.Value;
 Check(anchor.Into.Z==-sign,"Additive floor/ceiling cuts into the slab");
 var cuts=WallPieces.Build(p,anchor,"hatch");new AddObjectsCommand("Hatch",cuts).Do(p);
 Check(!new WorldGeometry(p,null).IsSolid(new(0,0,416)),"Cuts an opening through an additive slab from either side");
}
Console.WriteLine($"PASS {checks} floor and ceiling connector checks");
