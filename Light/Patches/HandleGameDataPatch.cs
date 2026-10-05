using HarmonyLib;
using Hazel;
using InnerNet;
using LightInDark.Utilities;
using Light.Utilities;
using static LightInDark.Utilities.LightUtils;

namespace Light.Patches;

// ⚠️⚠️ **停用原因（2026-10-04，用户实测「创建游戏连线区失败」）**
//
//   用户做了 A/B 对照：把主插件 Light.dll 改名后，**在线和本地都能正常建房**；
//   装回来就双双失败。→ 确认是主插件里的某个补丁。
//
//   本类是所有网络包的必经之路（`InnerNetClient.HandleGameData` 每个包都会走），
//   而这个 Prefix **把整包读一遍、再靠 `parentReader.Position = startPos` 还原**
//   —— 这是很脆弱的做法：
//     · Hazel 的 `MessageReader` 除 `Position` 外还有内部状态
//     · 读 string / packed-int 会**越过缓冲区边界**并改变内部状态
//     · 只还原 `Position` **不保证字节流真的复原**
//   → 下一句原版读取拿到错位数据 → 建房回包解析失败 → `GameId` 恒为 0
//     → `WaitWithTimeout` 超时 → `LastCustomDisconnect = "创建游戏连线区失败…"`
//       → `EnqueueDisconnect(Custom, "Couldn't connect")`（日志里那句）
//
//   也解释了"为什么 NikoCN1 这个服能连、别的不能"：取决于回包的字节布局。
//
//   以后要做同样的事（偷看被踢原因），**不能读原流的游标**，
//   应该复制一份字节再在副本上解析（`parentReader` 的缓冲区 memcpy 出来）。
// [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.HandleGameData))]
public static class HandleGameDataPatch
{
    public static void Prefix(InnerNetClient __instance, MessageReader parentReader)
    {
        if (__instance.AmHost || __instance.NetworkMode != NetworkModes.OnlineGame)
            return;

        int startPos = parentReader.Position;
        try
        {
            while (parentReader.BytesRemaining > 0)
            {
                int length = parentReader.ReadPackedInt32();
                byte tag = parentReader.ReadByte();
                if (tag == byte.MaxValue)
                {
                    byte flag = parentReader.ReadByte();
                    if (flag == 0)
                    {
                        string reason = parentReader.ReadString();
                        KickHelper.SetPendingReason(__instance.ClientId, reason);
                    }
                    break;
                }
                else
                {
                    // 普通数据块：跳过剩余数据（长度-1 字节，因为已读 tag）
                    int remaining = length - 1;
                    if (remaining > 0)
                        parentReader.Position += remaining;
                }
            }
        }
        catch
        {
            // 解析异常时静默忽略，避免影响游戏主流程
        }
        finally
        {
            // 将读取位置复原，保证后续正常处理
            parentReader.Position = startPos;
        }
    }
}