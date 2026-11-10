package com.claudecl.bridge;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import net.minecraftforge.client.event.ClientChatReceivedEvent;
import net.minecraftforge.eventbus.api.EventPriority;
import net.minecraftforge.eventbus.api.SubscribeEvent;

import java.util.ArrayDeque;

/**
 * 聊天与系统消息记录：指令的输出、聊天、动作栏提示都按顺序编号保存，最多保留 1000 条
 */
public final class ChatLog {
    private record Entry(long seq, long time, String type, String text) {
    }

    private static final ArrayDeque<Entry> ENTRIES = new ArrayDeque<>();
    private static long seq;
    private static long lastTime;

    private ChatLog() {
    }

    // 最低优先级并接收已取消的消息：即使被别的逻辑隐藏，也照样记录
    @SubscribeEvent(priority = EventPriority.LOWEST, receiveCanceled = true)
    public static void onChat(ClientChatReceivedEvent e) {
        if (e.getMessage() == null) return;
        String type = "chat";
        if (e instanceof ClientChatReceivedEvent.System sys) type = sys.isOverlay() ? "actionbar" : "system";
        add(type, e.getMessage().getString());
    }

    static synchronized void add(String type, String text) {
        seq++;
        lastTime = System.currentTimeMillis();
        ENTRIES.addLast(new Entry(seq, lastTime, type, text));
        while (ENTRIES.size() > 1000) ENTRIES.removeFirst();
    }

    static synchronized long lastSeq() {
        return seq;
    }

    static synchronized long lastTime() {
        return lastTime;
    }

    /** 取编号大于 since 的消息；actionbar 为 false 时跳过动作栏提示 */
    static synchronized JsonArray since(long since, int limit, boolean actionbar) {
        JsonArray arr = new JsonArray();
        int skip = 0;
        int total = 0;
        for (Entry e : ENTRIES) if (e.seq > since && (actionbar || !e.type.equals("actionbar"))) total++;
        if (limit > 0 && total > limit) skip = total - limit;
        for (Entry e : ENTRIES) {
            if (e.seq <= since || (!actionbar && e.type.equals("actionbar"))) continue;
            if (skip-- > 0) continue;
            JsonObject o = new JsonObject();
            o.addProperty("seq", e.seq);
            o.addProperty("time", e.time);
            o.addProperty("type", e.type);
            o.addProperty("text", e.text);
            arr.add(o);
        }
        return arr;
    }
}
