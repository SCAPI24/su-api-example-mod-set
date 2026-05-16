using System;
using System.Net;
using System.Text;
using Engine;

namespace Comms
{
    public class SuWriter : Writer
    {
        public void WritePoint3(Point3 point)
        {
            // 使用压缩整数写入三个坐标，减少数据大小
            WritePackedInt32(point.X);
            WritePackedInt32(point.Y);
            WritePackedInt32(point.Z);
        }

        // 可选：如果需要写入非压缩的Point3（如果坐标值通常很大）
        public void WritePoint3Fixed(Point3 point)
        {
            WriteInt32(point.X);
            WriteInt32(point.Y);
            WriteInt32(point.Z);
        }

        // 可选：批量写入Point3数组
        public void WritePoint3Array(Point3[] points)
        {
            if (points != null)
            {
                WritePackedInt32(points.Length);
                foreach (Point3 point in points)
                {
                    WritePoint3(point);
                }
            }
            else
            {
                WritePackedInt32(0);
            }
        }
        // 辅助方法：序列化 Vector2
        public void WriteVector2(SuWriter writer, Vector2 vector)
        {
            writer.WriteSingle(vector.X);
            writer.WriteSingle(vector.Y);
        }
        // 辅助方法：序列化 Vector3
        public void WriteVector3(SuWriter writer, Vector3 vector)
        {
            writer.WriteSingle(vector.X);
            writer.WriteSingle(vector.Y);
            writer.WriteSingle(vector.Z);
        }
        // 辅助方法：序列化 Quaternion
        public void WriteQuaternion(SuWriter writer, Quaternion quaternion)
        {
            writer.WriteSingle(quaternion.X);
            writer.WriteSingle(quaternion.Y);
            writer.WriteSingle(quaternion.Z);
            writer.WriteSingle(quaternion.W);
        }
        // 辅助方法：序列化 Ray3
        public void WriteRay3(SuWriter writer, Ray3 ray)
        {
            writer.WriteVector3(writer, ray.Position);
            writer.WriteVector3(writer, ray.Direction);
        }
    }
}