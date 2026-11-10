package com.claudecl.bridge;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.mojang.blaze3d.platform.NativeImage;
import net.minecraft.client.Minecraft;
import net.minecraft.client.Screenshot;
import net.minecraft.core.BlockPos;
import net.minecraftforge.client.ClientCommandHandler;

import javax.imageio.ImageIO;
import java.awt.RenderingHints;
import java.awt.image.BufferedImage;
import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.util.Base64;

/**
 * 请求分发：按操作名调用对应的功能
 */
final class Ops {
    private Ops() {
    }

    private static volatile boolean prepared;

    static JsonObject run(String op, JsonObject a) {
        prepare();
        return switch (op) {
            case "ping" -> new JsonObject();
            case "status" -> Info.status();
            case "quit_game" -> {
                // 正常关闭游戏（保存设置后退出）
                J.mc().execute(() -> J.mc().stop());
                yield new JsonObject();
            }

            // 存档与服务器
            case "list_worlds" -> Worlds.list();
            case "join_world" -> Worlds.join(a, timeout(a, 180));
            case "create_world" -> Worlds.create(a, timeout(a, 300));
            case "leave_world" -> Worlds.leave(timeout(a, 120));
            case "join_server" -> Worlds.joinServer(J.need(a, "address"), timeout(a, 60));

            // 指令与聊天
            case "run_command" -> runCommand(J.need(a, "command"), J.integer(a, "wait_ms", 1500));
            case "send_chat" -> sendChat(J.need(a, "message"));
            case "get_messages" -> messages(a);

            // 信息
            case "get_player" -> J.main(Info::player);
            case "get_inventory" -> J.main(() -> Info.inventory(J.bool(a, "include_empty", false)));
            case "get_item" -> J.main(() -> getItem(a));
            case "get_world" -> J.main(() -> Info.world(J.integer(a, "radius", 16)));
            case "get_block" -> J.main(() -> getBlock(a));
            case "find_blocks" -> J.main(() -> Info.findBlocks(J.need(a, "block"), J.integer(a, "radius", 16), J.integer(a, "limit", 20)), 20000);
            case "screenshot" -> screenshot(J.integer(a, "max_width", 960));

            // 操作角色
            case "look_at" -> J.main(() -> Actions.look(a));
            case "attack" -> Actions.attack(a);
            case "use_item" -> Actions.use(a);
            case "select_slot" -> J.main(() -> Actions.selectSlot(J.integer(a, "slot", 0)));
            case "hold_item" -> J.main(() -> Actions.holdItem(J.need(a, "item")));
            case "swap_slots" -> J.main(() -> Actions.swap(J.integer(a, "slot", -1), J.integer(a, "hotbar", -1)));
            case "drop_item" -> J.main(() -> Actions.drop(J.bool(a, "all", false), J.integer(a, "slot", -1)));
            case "give_item" -> giveItem(a);
            case "move" -> Actions.move(a);
            case "move_to" -> Actions.moveTo(a);
            case "respawn" -> J.main(Actions::respawn);

            // 容器与界面
            case "get_container" -> J.main(Actions::container);
            case "container_click" -> J.main(() -> Actions.containerClick(J.integer(a, "slot", -1), J.integer(a, "button", 0), J.str(a, "mode", "pickup")));
            case "get_screen" -> J.main(Screens::describe);
            case "click_widget" -> J.main(() -> Screens.click(a));
            case "click_screen" -> J.main(() -> Screens.clickAt(J.num(a, "x", 0), J.num(a, "y", 0), J.integer(a, "button", 0)));
            case "type_text" -> J.main(() -> Screens.type(a));
            case "close_screen" -> J.main(Screens::close);
            case "press_key" -> pressKey(a);
            case "list_keys" -> J.main(Actions::listKeys);
            default -> throw new BridgeException("不支持的操作：" + op);
        };
    }

    /**
     * 第一次收到请求时：关掉“窗口失去焦点就暂停”，否则 AI 操作时游戏窗口不在前台会自动暂停
     */
    private static void prepare() {
        if (prepared || Minecraft.getInstance() == null) return;
        prepared = true;
        Minecraft.getInstance().execute(() -> Minecraft.getInstance().options.pauseOnLostFocus = false);
    }

    private static long timeout(JsonObject a, int defSeconds) {
        return Math.max(5, Math.min(J.integer(a, "timeout_seconds", defSeconds), 900)) * 1000L;
    }

    // ---------- 指令与聊天 ----------

    /** 执行指令并收集它的输出：等到出现新消息、并且安静 200 毫秒，或者到达等待上限 */
    static JsonObject runCommand(String command, int waitMs) {
        String cmd = command.startsWith("/") ? command.substring(1) : command;
        long before = J.main(() -> {
            J.player();
            long seq = ChatLog.lastSeq();
            // 先交给客户端指令，没人处理再发给服务器
            if (!ClientCommandHandler.runCommand(cmd)) J.mc().player.connection.sendCommand(cmd);
            return seq;
        });
        JsonArray msgs = collect(before, Math.max(100, Math.min(waitMs, 30000)));
        JsonObject o = new JsonObject();
        o.addProperty("command", "/" + cmd);
        o.add("output", msgs);
        if (msgs.isEmpty()) o.addProperty("detail", "等待期间没有收到任何输出（有的指令本来就没有反馈）");
        return o;
    }

    private static JsonArray collect(long before, int waitMs) {
        long deadline = System.currentTimeMillis() + waitMs;
        while (System.currentTimeMillis() < deadline) {
            if (ChatLog.lastSeq() > before && System.currentTimeMillis() - ChatLog.lastTime() > 200) break;
            J.sleep(25);
        }
        return ChatLog.since(before, 200, false);
    }

    static JsonObject sendChat(String message) {
        if (message.startsWith("/")) return runCommand(message, 1500);
        long before = J.main(() -> {
            J.player();
            long seq = ChatLog.lastSeq();
            J.mc().player.connection.sendChat(message);
            return seq;
        });
        JsonObject o = new JsonObject();
        o.add("output", collect(before, 800));
        return o;
    }

    static JsonObject messages(JsonObject a) {
        long since = (long) J.num(a, "since", 0);
        JsonObject o = new JsonObject();
        o.add("messages", ChatLog.since(since, J.integer(a, "limit", 50), J.bool(a, "include_actionbar", false)));
        o.addProperty("last_seq", ChatLog.lastSeq());
        return o;
    }

    static JsonObject giveItem(JsonObject a) {
        String item = J.need(a, "item");
        if (!item.contains(":") && !item.contains("{")) item = "minecraft:" + item;
        int count = Math.max(1, J.integer(a, "count", 1));
        String nbt = J.str(a, "nbt", "");
        return runCommand("give @s " + item + nbt + " " + count, 1500);
    }

    // ---------- 信息 ----------

    private static JsonObject getItem(JsonObject a) {
        var p = J.player();
        String hand = J.str(a, "slot", "mainhand");
        int slot;
        switch (hand.toLowerCase()) {
            case "mainhand", "main_hand", "main" -> slot = p.getInventory().selected;
            case "offhand", "off_hand", "off" -> slot = 40;
            default -> {
                try {
                    slot = Integer.parseInt(hand);
                } catch (NumberFormatException e) {
                    throw new BridgeException("slot 是 0~40 的格子编号，或者 mainhand / offhand");
                }
            }
        }
        if (slot < 0 || slot >= p.getInventory().getContainerSize()) throw new BridgeException("slot 是 0~40 的格子编号");
        JsonObject o = Info.item(p.getInventory().getItem(slot), true);
        o.addProperty("slot", slot);
        o.addProperty("where", Info.slotName(slot));
        return o;
    }

    private static JsonObject getBlock(JsonObject a) {
        var p = J.player();
        BlockPos pos;
        if (J.has(a, "x")) {
            pos = BlockPos.containing(J.num(a, "x", 0), J.num(a, "y", 0), J.num(a, "z", 0));
        } else {
            JsonObject look = Info.lookingAt();
            if (!"block".equals(J.str(look, "type", ""))) throw new BridgeException("准星没有对着方块，请给出 x/y/z");
            JsonObject lp = look.getAsJsonObject("pos");
            pos = new BlockPos(lp.get("x").getAsInt(), lp.get("y").getAsInt(), lp.get("z").getAsInt());
        }
        return Info.block(J.mc().level, pos, true);
    }

    /** 截取当前画面，按比例缩小后以 JPEG 返回 */
    static JsonObject screenshot(int maxWidth) {
        byte[] png = J.main(() -> {
            Minecraft mc = J.mc();
            try (NativeImage img = Screenshot.takeScreenshot(mc.getMainRenderTarget())) {
                return img.asByteArray();
            } catch (Exception e) {
                throw new BridgeException("截图失败：" + e.getMessage());
            }
        });
        try {
            BufferedImage src = ImageIO.read(new ByteArrayInputStream(png));
            int w = src.getWidth(), h = src.getHeight();
            int tw = Math.max(64, Math.min(maxWidth, w));
            int th = Math.max(1, h * tw / w);
            BufferedImage dst = new BufferedImage(tw, th, BufferedImage.TYPE_INT_RGB);
            var g = dst.createGraphics();
            g.setRenderingHint(RenderingHints.KEY_INTERPOLATION, RenderingHints.VALUE_INTERPOLATION_BILINEAR);
            g.drawImage(src, 0, 0, tw, th, null);
            g.dispose();
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            ImageIO.write(dst, "jpg", out);
            JsonObject o = new JsonObject();
            o.addProperty("width", tw);
            o.addProperty("height", th);
            o.addProperty("original_width", w);
            o.addProperty("original_height", h);
            o.addProperty("mime", "image/jpeg");
            o.addProperty("image_base64", Base64.getEncoder().encodeToString(out.toByteArray()));
            return o;
        } catch (Exception e) {
            throw new BridgeException("处理截图失败：" + e.getMessage());
        }
    }

    // ---------- 按键 ----------

    private static JsonObject pressKey(JsonObject a) {
        JsonObject r = J.main(() -> Actions.pressKey(a));
        if (!r.has("_hold")) return r;
        int hold = r.get("_hold").getAsInt();
        JsonArray names = r.getAsJsonArray("_release_later");
        r.remove("_hold");
        r.remove("_release_later");
        try {
            Ticks.await(hold);
        } finally {
            J.run(() -> Actions.releaseMappings(names));
        }
        return r;
    }
}
