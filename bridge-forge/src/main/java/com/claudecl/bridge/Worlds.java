package com.claudecl.bridge;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import net.minecraft.FileUtil;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.screens.ConnectScreen;
import net.minecraft.client.gui.screens.GenericDirtMessageScreen;
import net.minecraft.client.gui.screens.LevelLoadingScreen;
import net.minecraft.client.gui.screens.ProgressScreen;
import net.minecraft.client.gui.screens.ReceivingLevelScreen;
import net.minecraft.client.gui.screens.Screen;
import net.minecraft.client.gui.screens.TitleScreen;
import net.minecraft.client.gui.screens.multiplayer.JoinMultiplayerScreen;
import net.minecraft.client.multiplayer.ServerData;
import net.minecraft.client.multiplayer.resolver.ServerAddress;
import net.minecraft.core.registries.Registries;
import net.minecraft.network.chat.Component;
import net.minecraft.world.Difficulty;
import net.minecraft.world.level.GameRules;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.LevelSettings;
import net.minecraft.world.level.WorldDataConfiguration;
import net.minecraft.world.level.levelgen.WorldOptions;
import net.minecraft.world.level.levelgen.presets.WorldPresets;
import net.minecraft.world.level.storage.LevelStorageSource;
import net.minecraft.world.level.storage.LevelSummary;

import java.util.List;
import java.util.OptionalLong;
import java.util.concurrent.TimeUnit;

/**
 * 存档与服务器：列出存档、进入、新建、退出、连接服务器
 */
final class Worlds {
    private Worlds() {
    }

    static JsonObject list() {
        Minecraft mc = J.mc();
        LevelStorageSource src = mc.getLevelSource();
        List<LevelSummary> list;
        try {
            list = src.loadLevelSummaries(src.findLevelCandidates()).get(30, TimeUnit.SECONDS);
        } catch (Exception e) {
            throw new BridgeException("读取存档列表失败：" + e.getMessage());
        }
        JsonArray arr = new JsonArray();
        list.stream().sorted((x, y) -> Long.compare(y.getLastPlayed(), x.getLastPlayed())).forEach(s -> {
            JsonObject o = new JsonObject();
            o.addProperty("folder", s.getLevelId());
            o.addProperty("name", s.getLevelName());
            o.addProperty("last_played", s.getLastPlayed());
            o.addProperty("game_mode", s.getGameMode().getName());
            o.addProperty("hardcore", s.isHardcore());
            o.addProperty("cheats", s.hasCheats());
            o.addProperty("version", s.getWorldVersionName().getString());
            arr.add(o);
        });
        JsonObject o = new JsonObject();
        o.addProperty("saves_dir", src.getBaseDir().toString());
        o.add("worlds", arr);
        return o;
    }

    /** 退出当前世界回到标题画面（单人世界会先保存） */
    static void disconnect(Minecraft mc) {
        if (mc.level == null) return;
        boolean local = mc.isLocalServer();
        mc.level.disconnect();
        if (local) mc.clearLevel(new GenericDirtMessageScreen(Component.translatable("menu.savingLevel")));
        else mc.clearLevel();
        mc.setScreen(new TitleScreen());
    }

    static JsonObject join(JsonObject a, long timeoutMs) {
        String id = J.need(a, "world");
        J.run(() -> {
            Minecraft mc = J.mc();
            if (mc.getOverlay() != null) throw new BridgeException("游戏还在加载资源，稍等再试");
            if (!mc.getLevelSource().levelExists(id))
                throw new BridgeException("找不到存档文件夹「" + id + "」，可以用 list_worlds 查看");
        });
        // 进入存档会长时间占用主线程，所以只排队、不等待结果
        J.mc().execute(() -> {
            Minecraft mc = J.mc();
            disconnect(mc);
            mc.createWorldOpenFlows().loadLevel(new TitleScreen(), id);
        });
        return waitInWorld(timeoutMs, "进入存档");
    }

    static JsonObject create(JsonObject a, long timeoutMs) {
        String name = J.str(a, "name", "AI World");
        GameType mode = switch (J.str(a, "game_mode", "creative").toLowerCase()) {
            case "survival" -> GameType.SURVIVAL;
            case "adventure" -> GameType.ADVENTURE;
            case "spectator" -> GameType.SPECTATOR;
            case "creative" -> GameType.CREATIVE;
            default -> throw new BridgeException("game_mode 只能是 survival、creative、adventure、spectator");
        };
        boolean hardcore = J.bool(a, "hardcore", false);
        boolean cheats = J.bool(a, "cheats", true);
        boolean flat = J.bool(a, "flat", false);
        Difficulty diff = Difficulty.byName(J.str(a, "difficulty", "normal").toLowerCase());
        if (diff == null) throw new BridgeException("difficulty 只能是 peaceful、easy、normal、hard");
        String seedText = J.str(a, "seed", "");
        long seed;
        if (seedText.isEmpty()) seed = WorldOptions.randomSeed();
        else {
            OptionalLong parsed = WorldOptions.parseSeed(seedText);
            seed = parsed.isPresent() ? parsed.getAsLong() : seedText.hashCode();
        }
        String folder = J.main(() -> {
            Minecraft mc = J.mc();
            if (mc.getOverlay() != null) throw new BridgeException("游戏还在加载资源，稍等再试");
            try {
                return FileUtil.findAvailableName(mc.getLevelSource().getBaseDir(), name, "");
            } catch (Exception e) {
                throw new BridgeException("存档名不可用：" + e.getMessage());
            }
        });
        LevelSettings settings = new LevelSettings(name, mode, hardcore, hardcore ? Difficulty.HARD : diff, cheats, new GameRules(), WorldDataConfiguration.DEFAULT);
        WorldOptions options = new WorldOptions(seed, J.bool(a, "structures", true), false);
        J.mc().execute(() -> {
            Minecraft mc = J.mc();
            disconnect(mc);
            mc.createWorldOpenFlows().createFreshLevel(folder, settings, options, reg -> flat
                    ? reg.registryOrThrow(Registries.WORLD_PRESET).getHolderOrThrow(WorldPresets.FLAT).value().createWorldDimensions()
                    : WorldPresets.createNormalWorldDimensions(reg));
        });
        JsonObject o = waitInWorld(timeoutMs, "新建存档");
        o.addProperty("folder", folder);
        o.addProperty("seed", seed);
        return o;
    }

    static JsonObject leave(long timeoutMs) {
        boolean was = J.main(() -> J.mc().level != null);
        JsonObject o = new JsonObject();
        if (!was) {
            o.addProperty("result", "not_in_world");
            return o;
        }
        J.mc().execute(() -> disconnect(J.mc()));
        long deadline = System.currentTimeMillis() + timeoutMs;
        while (System.currentTimeMillis() < deadline) {
            J.sleep(250);
            try {
                if (J.main(() -> J.mc().level == null && !(J.mc().screen instanceof GenericDirtMessageScreen), 2000)) {
                    o.addProperty("result", "left");
                    return o;
                }
            } catch (BridgeException ignored) {
                // 保存世界时主线程很忙，继续等
            }
        }
        throw new BridgeException("退出世界超时（可能还在保存）");
    }

    static JsonObject joinServer(String address, long timeoutMs) {
        if (!ServerAddress.isValidAddress(address)) throw new BridgeException("服务器地址格式不对：" + address);
        J.mc().execute(() -> {
            Minecraft mc = J.mc();
            disconnect(mc);
            ServerData data = new ServerData(address, address, false);
            ConnectScreen.startConnecting(new JoinMultiplayerScreen(new TitleScreen()), mc, ServerAddress.parseString(address), data, false);
        });
        return waitInWorld(timeoutMs, "连接服务器");
    }

    private static boolean isLoadingScreen(Screen s) {
        return s instanceof LevelLoadingScreen || s instanceof ReceivingLevelScreen || s instanceof GenericDirtMessageScreen
                || s instanceof ProgressScreen || s instanceof ConnectScreen;
    }

    /**
     * 等待玩家真正进入世界。中途出现需要确认的界面（例如版本提示、断开连接）时立刻返回，
     * 让调用方用 get_screen / click_widget 处理
     */
    private static JsonObject waitInWorld(long timeoutMs, String what) {
        long start = System.currentTimeMillis();
        long deadline = start + timeoutMs;
        J.sleep(300);
        while (System.currentTimeMillis() < deadline) {
            JsonObject state;
            try {
                state = J.main(() -> {
                    Minecraft mc = J.mc();
                    Screen s = mc.screen;
                    JsonObject o = new JsonObject();
                    if (mc.player != null && mc.level != null && (s == null || !isLoadingScreen(s))) {
                        o.addProperty("result", "in_world");
                        return o;
                    }
                    if (s != null && !isLoadingScreen(s) && !(s instanceof TitleScreen) && System.currentTimeMillis() - start > 1500) {
                        o.addProperty("result", "needs_attention");
                        o.add("screen", Screens.describe());
                        return o;
                    }
                    return null;
                }, 2000);
            } catch (BridgeException e) {
                state = null; // 加载中主线程繁忙，继续等
            }
            if (state != null) {
                if (state.get("result").getAsString().equals("in_world")) {
                    J.sleep(500);
                    JsonObject st = Info.status();
                    st.addProperty("result", "in_world");
                    st.addProperty("seconds", (System.currentTimeMillis() - start) / 1000);
                    return st;
                }
                state.addProperty("detail", what + "时出现了一个界面，需要处理后才能继续");
                return state;
            }
            J.sleep(400);
        }
        throw new BridgeException(what + "超时（" + timeoutMs / 1000 + " 秒），可以用 status 查看进度");
    }
}
