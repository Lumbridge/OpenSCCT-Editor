using System.Text;

internal static class SyntheticFixtures
{
    public static string ActorX(string folder, bool animation)
    {
        string path = Path.Combine(folder, animation ? "synthetic.psa" : "synthetic.psk");
        using var file = new BinaryWriter(File.Create(path));
        void Chunk(string name, int size, int count, Action<BinaryWriter> write)
        {
            file.Write(Encoding.ASCII.GetBytes(name.PadRight(20, '\0'))); file.Write(0); file.Write(size); file.Write(count); write(file);
        }
        void Name(BinaryWriter writer, string value, int width) => writer.Write(Encoding.ASCII.GetBytes(value.PadRight(width, '\0')));
        Chunk(animation ? "ANIMHEAD" : "ACTRHEAD", 0, 0, _ => { });
        Chunk(animation ? "BONENAMES" : "REFSKELT", 120, 1, w =>
        {
            Name(w, "Root", 64); w.Write(0); w.Write(0); w.Write(0);
            w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f);
            for (int i = 0; i < 7; i++) w.Write(0f);
        });
        if (animation)
        {
            Chunk("ANIMINFO", 168, 1, w =>
            {
                Name(w, "Move", 64); Name(w, "Default", 64); w.Write(1); w.Write(0); w.Write(0); w.Write(0); w.Write(0f); w.Write(1f); w.Write(30f); w.Write(0); w.Write(0); w.Write(2);
            });
            Chunk("ANIMKEYS", 32, 2, w =>
            {
                for (int frame = 0; frame < 2; frame++) { w.Write(frame * 100f); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); w.Write(1f / 30); }
            });
        }
        else
        {
            Chunk("PNTS0000", 12, 3, w => { foreach (float value in new[] { 0f, 0, 0, 100, 0, 0, 0, 0, 100 }) w.Write(value); });
            Chunk("VTXW0000", 16, 3, w => { for (uint i = 0; i < 3; i++) { w.Write(i); w.Write(i == 1 ? 1f : 0); w.Write(i == 2 ? 1f : 0); w.Write(0); } });
            Chunk("FACE0000", 12, 1, w => { w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2); w.Write((byte)0); w.Write((byte)0); w.Write(1); });
            Chunk("RAWWEIGHTS", 12, 3, w => { for (int i = 0; i < 3; i++) { w.Write(1f); w.Write(i); w.Write(0); } });
        }
        return path;
    }
    public static string Fbx(string folder)
    {
        string path = Path.Combine(folder, "synthetic.fbx");
        File.WriteAllText(path, """
; FBX 7.4.0 project file
FBXHeaderExtension:  {
 FBXHeaderVersion: 1003
 FBXVersion: 7400
 Creator: "OpenSCCT synthetic animation fixture"
}
GlobalSettings:  {
 Version: 1000
 Properties70:  {
  P: "UpAxis", "int", "Integer", "",1
  P: "UpAxisSign", "int", "Integer", "",1
  P: "FrontAxis", "int", "Integer", "",2
  P: "FrontAxisSign", "int", "Integer", "",-1
  P: "CoordAxis", "int", "Integer", "",0
  P: "CoordAxisSign", "int", "Integer", "",1
  P: "UnitScaleFactor", "double", "Number", "",1
  P: "TimeMode", "enum", "", "",6
 }
}
Objects:  {
 Model: 1, "Model::Root", "LimbNode" {
  Version: 232
  Properties70:  {
   P: "Lcl Translation", "Lcl Translation", "", "A",0,0,0
   P: "Lcl Rotation", "Lcl Rotation", "", "A",0,0,0
   P: "Lcl Scaling", "Lcl Scaling", "", "A",1,1,1
  }
  Shading: T
  Culling: "CullingOff"
 }
 AnimationStack: 2, "AnimStack::Move", "" {
  Properties70:  {
   P: "LocalStart", "KTime", "Time", "",0
   P: "LocalStop", "KTime", "Time", "",46186158000
   P: "ReferenceStart", "KTime", "Time", "",0
   P: "ReferenceStop", "KTime", "Time", "",46186158000
  }
 }
 AnimationLayer: 3, "AnimLayer::BaseLayer", "" {
 }
 AnimationCurveNode: 4, "AnimCurveNode::T", "" {
  Properties70:  {
   P: "d|X", "Number", "", "A",0
   P: "d|Y", "Number", "", "A",0
   P: "d|Z", "Number", "", "A",0
  }
 }
 AnimationCurve: 5, "AnimCurve::X", "" {
  Default: 0
  KeyVer: 4008
  KeyTime: *2 {
   a: 0,46186158000
  }
  KeyValueFloat: *2 {
   a: 0,100
  }
  KeyAttrFlags: *1 {
   a: 24836
  }
  KeyAttrDataFloat: *4 {
   a: 0,0,255790911,0
  }
  KeyAttrRefCount: *1 {
   a: 2
  }
 }
}
Connections:  {
 C: "OO",1,0
 C: "OO",3,2
 C: "OO",4,3
 C: "OP",4,1,"Lcl Translation"
 C: "OP",5,4,"d|X"
}
Takes:  {
 Current: "Move"
 Take: "Move" {
  FileName: "Move.tak"
  LocalTime: 0,46186158000
  ReferenceTime: 0,46186158000
 }
}
""");
        return path;
    }
}
