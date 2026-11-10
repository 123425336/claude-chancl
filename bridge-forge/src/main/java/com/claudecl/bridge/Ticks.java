package com.claudecl.bridge;

import net.minecraftforge.event.TickEvent;
import net.minecraftforge.eventbus.api.SubscribeEvent;

/**
 * 客户端计时：记录已经过去的游戏刻，用来实现“按住若干刻”之类的操作
 */
public final class Ticks {
    private static volatile long count;

    private Ticks() {
    }

    @SubscribeEvent
    public static void onTick(TickEvent.ClientTickEvent e) {
        if (e.phase == TickEvent.Phase.END) count++;
    }

    static long now() {
        return count;
    }

    /** 在调用线程上等待 n 个游戏刻（不能在游戏主线程上调用） */
    static void await(int n) {
        long target = count + Math.max(1, n);
        long deadline = System.currentTimeMillis() + n * 250L + 5000;
        while (count < target) {
            if (System.currentTimeMillis() > deadline) throw new BridgeException("游戏没有在走（可能卡住或正在加载）");
            J.sleep(5);
        }
    }
}
