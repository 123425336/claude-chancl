package com.claudecl.bridge;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import net.minecraft.client.Minecraft;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.core.BlockPos;
import net.minecraft.world.phys.Vec3;

import java.util.concurrent.ExecutionException;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.function.Supplier;

/**
 * 常用小工具：读取参数、切到游戏主线程执行、拿到当前玩家
 */
final class J {
    private J() {
    }

    static Minecraft mc() {
        Minecraft m = Minecraft.getInstance();
        if (m == null) throw new BridgeException("游戏还在启动，请稍等");
        return m;
    }

    /** 在游戏主线程上执行并取回结果（默认最多等 10 秒） */
    static <T> T main(Supplier<T> task) {
        return main(task, 10000);
    }

    static <T> T main(Supplier<T> task, long timeoutMs) {
        Minecraft m = mc();
        if (m.isSameThread()) return task.get();
        try {
            return m.submit(task).get(timeoutMs, TimeUnit.MILLISECONDS);
        } catch (TimeoutException e) {
            throw new BridgeException("游戏主线程暂时没有响应（可能正在加载或保存）");
        } catch (ExecutionException e) {
            Throwable c = e.getCause();
            if (c instanceof RuntimeException r) throw r;
            if (c instanceof Error err) throw err;
            throw new RuntimeException(c);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new BridgeException("操作被中断");
        }
    }

    /** 在主线程上执行没有返回值的操作 */
    static void run(Runnable task) {
        main(() -> {
            task.run();
            return Boolean.TRUE;
        });
    }

    static LocalPlayer player() {
        LocalPlayer p = mc().player;
        if (p == null || mc().level == null || mc().gameMode == null)
            throw new BridgeException("还没有进入世界：请先用 join_world 进入存档，或用 create_world 新建一个");
        return p;
    }

    /** 需要在没有打开界面时才能做的操作 */
    static void needNoScreen() {
        if (mc().screen != null)
            throw new BridgeException("当前打开着界面「" + mc().screen.getTitle().getString() + "」，先用 close_screen 关掉，或用 get_screen 查看");
    }

    static void sleep(long ms) {
        try {
            Thread.sleep(ms);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new BridgeException("操作被中断");
        }
    }

    // ---------- 读取参数 ----------

    static boolean has(JsonObject o, String key) {
        return o.has(key) && !o.get(key).isJsonNull();
    }

    static String str(JsonObject o, String key, String def) {
        if (!has(o, key)) return def;
        JsonElement e = o.get(key);
        return e.isJsonPrimitive() ? e.getAsString() : e.toString();
    }

    static int integer(JsonObject o, String key, int def) {
        if (!has(o, key)) return def;
        try {
            return (int) Math.round(o.get(key).getAsDouble());
        } catch (Exception e) {
            throw new BridgeException("参数 " + key + " 需要是整数");
        }
    }

    static double num(JsonObject o, String key, double def) {
        if (!has(o, key)) return def;
        try {
            return o.get(key).getAsDouble();
        } catch (Exception e) {
            throw new BridgeException("参数 " + key + " 需要是数字");
        }
    }

    static boolean bool(JsonObject o, String key, boolean def) {
        if (!has(o, key)) return def;
        JsonElement e = o.get(key);
        if (e.isJsonPrimitive() && e.getAsJsonPrimitive().isString()) return Boolean.parseBoolean(e.getAsString());
        return e.getAsBoolean();
    }

    static String need(JsonObject o, String key) {
        String v = str(o, key, null);
        if (v == null || v.isEmpty()) throw new BridgeException("缺少参数 " + key);
        return v;
    }

    /** 数字是否写成了整数（例如 12 而不是 12.5），用来判断坐标是方块还是精确位置 */
    static boolean isWhole(JsonObject o, String key) {
        if (!has(o, key)) return false;
        String s = o.get(key).getAsJsonPrimitive().getAsNumber().toString();
        return !s.contains(".") && !s.contains("e") && !s.contains("E");
    }

    // ---------- 生成结果 ----------

    static JsonObject vec(Vec3 v) {
        JsonObject o = new JsonObject();
        o.addProperty("x", round(v.x));
        o.addProperty("y", round(v.y));
        o.addProperty("z", round(v.z));
        return o;
    }

    static JsonObject pos(BlockPos p) {
        JsonObject o = new JsonObject();
        o.addProperty("x", p.getX());
        o.addProperty("y", p.getY());
        o.addProperty("z", p.getZ());
        return o;
    }

    static double round(double v) {
        return Math.round(v * 100.0) / 100.0;
    }

    static JsonArray arr(Iterable<String> items) {
        JsonArray a = new JsonArray();
        for (String s : items) a.add(s);
        return a;
    }
}
