package com.claudecl.bridge;

import com.mojang.logging.LogUtils;
import net.minecraftforge.api.distmarker.Dist;
import net.minecraftforge.common.MinecraftForge;
import net.minecraftforge.fml.common.Mod;
import net.minecraftforge.fml.loading.FMLEnvironment;
import org.slf4j.Logger;

/**
 * 桥接模组入口：只在客户端启动本机桥接服务，并挂上聊天记录与计时监听
 */
@Mod(BridgeMod.ID)
public final class BridgeMod {
    public static final String ID = "claudecl_bridge";
    public static final String VERSION = "1.0.0";
    public static final Logger LOG = LogUtils.getLogger();

    public BridgeMod() {
        // 专用服务器上什么都不做
        if (FMLEnvironment.dist != Dist.CLIENT) return;
        MinecraftForge.EVENT_BUS.register(ChatLog.class);
        MinecraftForge.EVENT_BUS.register(Ticks.class);
        BridgeServer.start();
    }
}
