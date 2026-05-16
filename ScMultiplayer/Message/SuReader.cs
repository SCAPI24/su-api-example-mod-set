using System;
using System.Net;
using System.Text;
using Engine;

namespace Comms
{
    public class SuReader : Reader
    {
        public SuReader(byte[] bytes) : base(bytes)
        {
        }

        public Point3 ReadPoint3()
        {
            int x = ReadPackedInt32();
            int y = ReadPackedInt32();
            int z = ReadPackedInt32();
            return new Point3(x, y, z);
        }

        // 可选：读取非压缩的Point3
        public Point3 ReadPoint3Fixed()
        {
            int x = ReadInt32();
            int y = ReadInt32();
            int z = ReadInt32();
            return new Point3(x, y, z);
        }

        // 可选：批量读取Point3数组
        public Point3[] ReadPoint3Array()
        {
            int count = ReadPackedInt32();
            if (count == 0)
                return Array.Empty<Point3>();

            Point3[] points = new Point3[count];
            for (int i = 0; i < count; i++)
            {
                points[i] = ReadPoint3();
            }
            return points;
        }
        // 辅助方法：序列化 Vector2
        public Vector2 ReadVector2(SuReader reader)
        {
            return new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }
        // 辅助方法：序列化 Vector3
        public Vector3 ReadVector3(SuReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        // 辅助方法：序列化 Quaternion
        public Quaternion ReadQuaternion(SuReader reader)
        {
            return new Quaternion(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );
        }
        // 辅助方法：序列化 Ray3
        public Ray3 ReadRay3(SuReader reader)
        {
            Vector3 position = ReadVector3(reader);
            Vector3 direction = ReadVector3(reader);
            return new Ray3(position, direction);
        }



    }
}