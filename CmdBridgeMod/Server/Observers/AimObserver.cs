using Engine;
using Game;
using System;
using System.Collections.Generic;

namespace CmdBridgeMod
{
    /// <summary>
    /// 准星指向什么：把玩家意图射线（PlayerInput.Aim/Dig/Hit/Interact）
    /// 或相机视线解析成具体的方块/实体，这就是"点击的对象（世界侧）"。全程只读。
    /// </summary>
    internal static class AimObserver
    {
        public static Dictionary<string, object> Describe(float maxDistance)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["loaded"] = false;

            if (GameManager.Project == null)
                return result;

            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null || players.ComponentPlayers.Count == 0)
                return result;

            ComponentPlayer player = players.ComponentPlayers[0];
            result["loaded"] = true;

            Ray3 ray;
            string source;
            if (!TryGetIntentRay(player, out ray, out source))
            {
                Camera camera = player.GameWidget.ActiveCamera;
                if (camera == null)
                {
                    result["active"] = false;
                    return result;
                }
                ray = new Ray3(camera.ViewPosition, camera.ViewDirection);
                source = "camera";
            }

            result["active"] = true;
            result["source"] = source;
            result["origin"] = PlayerObserver.Vector3ToDictionary(ray.Position);
            result["direction"] = PlayerObserver.Vector3ToDictionary(ray.Direction);

            float distance = maxDistance > 0f ? maxDistance : 8f;
            Vector3 end = ray.Position + ray.Direction * distance;

            result["target"] = ResolveTarget(ray, end, player);
            return result;
        }

        private static bool TryGetIntentRay(ComponentPlayer player, out Ray3 ray, out string source)
        {
            ray = default;
            source = null;
            try
            {
                PlayerInput input = player.ComponentInput.PlayerInput;
                if (input.Aim.HasValue) { ray = input.Aim.Value; source = "aim"; return true; }
                if (input.Dig.HasValue) { ray = input.Dig.Value; source = "dig"; return true; }
                if (input.Hit.HasValue) { ray = input.Hit.Value; source = "hit"; return true; }
                if (input.Interact.HasValue) { ray = input.Interact.Value; source = "interact"; return true; }
            }
            catch
            {
            }
            return false;
        }

        private static Dictionary<string, object> ResolveTarget(
            Ray3 ray, Vector3 end, ComponentPlayer player)
        {
            var target = new Dictionary<string, object>(StringComparer.Ordinal);

            // 方块
            try
            {
                SubsystemTerrain terrain = GameManager.Project.FindSubsystem<SubsystemTerrain>(false);
                if (terrain != null && terrain.Terrain != null)
                {
                    TerrainRaycastResult? hit = terrain.Raycast(
                        ray.Position, end, true, true, (value, distance) => true);
                    if (hit.HasValue)
                    {
                        TerrainRaycastResult value = hit.Value;
                        int contents = Terrain.ExtractContents(value.Value);
                        var block = BlocksManager.Blocks[contents];
                        target["kind"] = "block";
                        target["cell"] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["x"] = value.CellFace.X,
                            ["y"] = value.CellFace.Y,
                            ["z"] = value.CellFace.Z
                        };
                        target["face"] = value.CellFace.Face;
                        target["value"] = value.Value;
                        target["contents"] = contents;
                        // v1 用方块类型名（例如 DirtBlock）作为可读标识。
                        target["blockType"] = block != null ? block.GetType().Name : null;
                        target["distance"] = value.Distance;
                        target["hitPoint"] = PlayerObserver.Vector3ToDictionary(value.HitPoint());
                        return target;
                    }
                }
            }
            catch
            {
            }

            // 实体
            try
            {
                SubsystemBodies bodies = GameManager.Project.FindSubsystem<SubsystemBodies>(false);
                if (bodies != null)
                {
                    // 必须排除玩家自身，否则射线起点在自身碰撞盒内会命中自己（distance=0）。
                    ComponentBody self = player != null ? player.ComponentBody : null;
                    BodyRaycastResult? hit = bodies.Raycast(
                        ray.Position, end, 0f, (body, distance) => !ReferenceEquals(body, self));
                    if (hit.HasValue)
                    {
                        BodyRaycastResult value = hit.Value;
                        target["kind"] = "body";
                        target["type"] = value.ComponentBody != null
                            ? value.ComponentBody.Entity.GetType().Name
                            : null;
                        target["distance"] = value.Distance;
                        target["hitPoint"] = PlayerObserver.Vector3ToDictionary(value.HitPoint());
                        return target;
                    }
                }
            }
            catch
            {
            }

            target["kind"] = "none";
            return target;
        }
    }
}
