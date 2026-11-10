package com.claudecl.bridge;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import net.minecraft.SharedConstants;
import net.minecraft.client.Minecraft;
import net.minecraft.client.multiplayer.ClientLevel;
import net.minecraft.client.multiplayer.PlayerInfo;
import net.minecraft.client.multiplayer.ServerData;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.core.BlockPos;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.network.chat.Component;
import net.minecraft.world.effect.MobEffectInstance;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.item.ItemEntity;
import net.minecraft.world.entity.player.Inventory;
import net.minecraft.world.food.FoodProperties;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.TooltipFlag;
import net.minecraft.world.item.enchantment.Enchantment;
import net.minecraft.world.item.enchantment.EnchantmentHelper;
import net.minecraft.world.level.LightLayer;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.block.state.properties.Property;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.EntityHitResult;
import net.minecraft.world.phys.HitResult;
import net.minecraft.world.phys.Vec3;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Map;

/**
 * 读取游戏信息：状态、玩家、背包、物品、世界、方块。都需要在主线程上调用
 */
final class Info {
    private Info() {
    }

    // ---------- 总体状态 ----------

    static JsonObject status() {
        JsonObject o = new JsonObject();
        o.addProperty("bridge_version", BridgeMod.VERSION);
        o.addProperty("game_version", SharedConstants.getCurrentVersion().getName());
        Minecraft mc = Minecraft.getInstance();
        if (mc == null || mc.getOverlay() != null) {
            o.addProperty("state", "loading");
            o.addProperty("hint", "游戏还在加载资源，稍等再试");
            return o;
        }
        try {
            JsonObject detail = J.main(Info::statusOnMain, 3000);
            for (var e : detail.entrySet()) o.add(e.getKey(), e.getValue());
        } catch (BridgeException e) {
            o.addProperty("state", "busy");
            o.addProperty("hint", e.getMessage());
        }
        return o;
    }

    private static JsonObject statusOnMain() {
        Minecraft mc = J.mc();
        JsonObject o = new JsonObject();
        boolean inWorld = mc.player != null && mc.level != null;
        // 资源加载遮罩还在、或者还没有任何界面时，说明游戏仍在加载
        boolean loading = mc.getOverlay() != null || (!inWorld && mc.screen == null);
        o.addProperty("state", loading ? "loading" : inWorld ? "in_world" : "menu");
        o.addProperty("fps", mc.getFps());
        o.addProperty("paused", mc.isPaused());
        o.addProperty("window_focused", mc.isWindowActive());
        if (mc.screen != null) o.add("screen", Screens.brief(mc.screen));
        if (inWorld) {
            o.addProperty("player", mc.player.getGameProfile().getName());
            o.addProperty("game_mode", mc.gameMode.getPlayerMode().getName());
            o.addProperty("dimension", mc.level.dimension().location().toString());
            if (mc.getSingleplayerServer() != null) {
                o.addProperty("world_type", "singleplayer");
                o.addProperty("world_name", mc.getSingleplayerServer().getWorldData().getLevelName());
                o.addProperty("world_folder", mc.getSingleplayerServer().getWorldPath(net.minecraft.world.level.storage.LevelResource.ROOT).normalize().getFileName().toString());
                o.addProperty("cheats", mc.getSingleplayerServer().getWorldData().getAllowCommands());
            } else {
                ServerData sd = mc.getCurrentServer();
                o.addProperty("world_type", "multiplayer");
                if (sd != null) o.addProperty("server", sd.ip);
            }
        }
        o.addProperty("messages_seq", ChatLog.lastSeq());
        return o;
    }

    // ---------- 玩家 ----------

    static JsonObject player() {
        Minecraft mc = J.mc();
        LocalPlayer p = J.player();
        JsonObject o = new JsonObject();
        o.addProperty("name", p.getGameProfile().getName());
        o.addProperty("uuid", p.getUUID().toString());
        o.add("pos", J.vec(p.position()));
        o.add("block_pos", J.pos(p.blockPosition()));
        o.addProperty("yaw", J.round(p.getYRot()));
        o.addProperty("pitch", J.round(p.getXRot()));
        o.addProperty("facing", p.getDirection().getName());
        o.addProperty("dimension", p.level().dimension().location().toString());
        o.addProperty("game_mode", mc.gameMode.getPlayerMode().getName());
        o.addProperty("health", J.round(p.getHealth()));
        o.addProperty("max_health", J.round(p.getMaxHealth()));
        o.addProperty("absorption", J.round(p.getAbsorptionAmount()));
        o.addProperty("food", p.getFoodData().getFoodLevel());
        o.addProperty("saturation", J.round(p.getFoodData().getSaturationLevel()));
        o.addProperty("armor", p.getArmorValue());
        o.addProperty("air", p.getAirSupply());
        o.addProperty("xp_level", p.experienceLevel);
        o.addProperty("xp_progress", J.round(p.experienceProgress));
        o.addProperty("on_ground", p.onGround());
        o.addProperty("in_water", p.isInWater());
        o.addProperty("sprinting", p.isSprinting());
        o.addProperty("sneaking", p.isShiftKeyDown());
        o.addProperty("dead", p.isDeadOrDying());
        o.addProperty("using_item", p.isUsingItem());
        o.addProperty("attack_cooldown", J.round(p.getAttackStrengthScale(0.5F)));
        o.addProperty("selected_slot", p.getInventory().selected);
        o.add("main_hand", item(p.getMainHandItem(), false));
        o.add("off_hand", item(p.getOffhandItem(), false));
        JsonArray effects = new JsonArray();
        for (MobEffectInstance e : p.getActiveEffects()) {
            JsonObject eo = new JsonObject();
            eo.addProperty("id", String.valueOf(BuiltInRegistries.MOB_EFFECT.getKey(e.getEffect())));
            eo.addProperty("name", e.getEffect().getDisplayName().getString());
            eo.addProperty("level", e.getAmplifier() + 1);
            eo.addProperty("seconds", e.isInfiniteDuration() ? -1 : e.getDuration() / 20);
            effects.add(eo);
        }
        o.add("effects", effects);
        o.add("looking_at", lookingAt());
        return o;
    }

    /** 准星对着的东西 */
    static JsonObject lookingAt() {
        Minecraft mc = J.mc();
        mc.gameRenderer.pick(1.0F);
        HitResult hit = mc.hitResult;
        JsonObject o = new JsonObject();
        if (hit == null || hit.getType() == HitResult.Type.MISS) {
            o.addProperty("type", "none");
            return o;
        }
        if (hit instanceof BlockHitResult b) {
            o.addProperty("type", "block");
            o.add("pos", J.pos(b.getBlockPos()));
            o.addProperty("face", b.getDirection().getName());
            o.add("block", block(mc.level, b.getBlockPos(), false));
        } else if (hit instanceof EntityHitResult e) {
            o.addProperty("type", "entity");
            o.add("entity", entity(e.getEntity(), mc.player));
        }
        o.addProperty("distance", J.round(hit.getLocation().distanceTo(mc.player.getEyePosition())));
        return o;
    }

    // ---------- 背包与物品 ----------

    /** 背包格子编号：0~8 快捷栏，9~35 背包，36~39 盔甲（脚、腿、胸、头），40 副手 */
    static String slotName(int i) {
        if (i < 9) return "快捷栏 " + (i + 1);
        if (i < 36) return "背包";
        if (i == 36) return "靴子";
        if (i == 37) return "护腿";
        if (i == 38) return "胸甲";
        if (i == 39) return "头盔";
        return "副手";
    }

    static JsonObject inventory(boolean includeEmpty) {
        LocalPlayer p = J.player();
        Inventory inv = p.getInventory();
        JsonObject o = new JsonObject();
        o.addProperty("selected_slot", inv.selected);
        JsonArray items = new JsonArray();
        int used = 0;
        for (int i = 0; i < inv.getContainerSize(); i++) {
            ItemStack s = inv.getItem(i);
            if (s.isEmpty() && !includeEmpty) continue;
            if (!s.isEmpty() && i < 36) used++;
            JsonObject io = item(s, false);
            io.addProperty("slot", i);
            io.addProperty("where", slotName(i));
            items.add(io);
        }
        o.addProperty("used_slots", used);
        o.addProperty("free_slots", 36 - used);
        o.add("items", items);
        ItemStack carried = p.containerMenu.getCarried();
        if (!carried.isEmpty()) o.add("carried", item(carried, false));
        o.addProperty("slot_help", "0~8 快捷栏，9~35 背包，36~39 盔甲（脚、腿、胸、头），40 副手");
        return o;
    }

    static JsonObject item(ItemStack s, boolean detailed) {
        JsonObject o = new JsonObject();
        if (s.isEmpty()) {
            o.addProperty("id", "minecraft:air");
            o.addProperty("count", 0);
            return o;
        }
        o.addProperty("id", String.valueOf(BuiltInRegistries.ITEM.getKey(s.getItem())));
        o.addProperty("name", s.getHoverName().getString());
        o.addProperty("count", s.getCount());
        if (s.isDamageableItem()) {
            o.addProperty("durability", s.getMaxDamage() - s.getDamageValue());
            o.addProperty("max_durability", s.getMaxDamage());
        }
        Map<Enchantment, Integer> ench = EnchantmentHelper.getEnchantments(s);
        if (!ench.isEmpty()) {
            JsonArray ea = new JsonArray();
            for (var e : ench.entrySet()) {
                JsonObject eo = new JsonObject();
                eo.addProperty("id", String.valueOf(BuiltInRegistries.ENCHANTMENT.getKey(e.getKey())));
                eo.addProperty("name", e.getKey().getFullname(e.getValue()).getString());
                eo.addProperty("level", e.getValue());
                ea.add(eo);
            }
            o.add("enchantments", ea);
        }
        if (!detailed) return o;

        Minecraft mc = J.mc();
        o.addProperty("max_stack", s.getMaxStackSize());
        o.addProperty("rarity", s.getRarity().name().toLowerCase());
        o.addProperty("stackable", s.isStackable());
        if (s.isEdible()) {
            FoodProperties f = s.getFoodProperties(mc.player);
            if (f != null) {
                JsonObject fo = new JsonObject();
                fo.addProperty("nutrition", f.getNutrition());
                fo.addProperty("saturation", J.round(f.getSaturationModifier()));
                fo.addProperty("always_edible", f.canAlwaysEat());
                o.add("food", fo);
            }
        }
        // 物品提示文字里包含了攻击伤害、属性、说明等信息
        List<String> tooltip = new ArrayList<>();
        try {
            for (Component c : s.getTooltipLines(mc.player, TooltipFlag.NORMAL)) tooltip.add(c.getString());
        } catch (Exception e) {
            tooltip.add("（读取提示文字失败：" + e.getMessage() + "）");
        }
        o.add("tooltip", J.arr(tooltip));
        List<String> tags = new ArrayList<>();
        s.getTags().forEach(t -> tags.add(t.location().toString()));
        o.add("tags", J.arr(tags));
        if (s.getTag() != null) {
            String nbt = s.getTag().toString();
            o.addProperty("nbt", nbt.length() > 4000 ? nbt.substring(0, 4000) + "…（已截断）" : nbt);
        }
        return o;
    }

    // ---------- 世界 ----------

    static JsonObject world(int radius) {
        Minecraft mc = J.mc();
        LocalPlayer p = J.player();
        ClientLevel level = mc.level;
        BlockPos at = p.blockPosition();
        JsonObject o = new JsonObject();
        o.addProperty("dimension", level.dimension().location().toString());
        long day = level.getDayTime();
        o.addProperty("day", day / 24000);
        o.addProperty("time_of_day", day % 24000);
        o.addProperty("is_day", day % 24000 < 12542 || day % 24000 > 23460);
        o.addProperty("game_time", level.getGameTime());
        o.addProperty("raining", level.isRaining());
        o.addProperty("thundering", level.isThundering());
        o.addProperty("difficulty", level.getDifficulty().getKey());
        o.addProperty("biome", level.getBiome(at).unwrapKey().map(k -> k.location().toString()).orElse("unknown"));
        o.addProperty("block_light", level.getBrightness(LightLayer.BLOCK, at));
        o.addProperty("sky_light", level.getBrightness(LightLayer.SKY, at));
        o.add("spawn", J.pos(level.getSharedSpawnPos()));
        if (mc.getSingleplayerServer() != null) {
            o.addProperty("world_name", mc.getSingleplayerServer().getWorldData().getLevelName());
            o.addProperty("seed", mc.getSingleplayerServer().getWorldData().worldGenOptions().seed());
        }
        if (mc.getConnection() != null) {
            JsonArray players = new JsonArray();
            for (PlayerInfo pi : mc.getConnection().getOnlinePlayers()) {
                JsonObject po = new JsonObject();
                po.addProperty("name", pi.getProfile().getName());
                po.addProperty("latency", pi.getLatency());
                if (pi.getGameMode() != null) po.addProperty("game_mode", pi.getGameMode().getName());
                players.add(po);
            }
            o.add("online_players", players);
        }
        // 附近的实体按距离排序，最多列 40 个
        int r = Math.max(1, Math.min(radius, 64));
        List<Entity> list = new ArrayList<>(level.getEntities(p, p.getBoundingBox().inflate(r)));
        list.sort(Comparator.comparingDouble(e -> e.distanceToSqr(p)));
        JsonArray ents = new JsonArray();
        for (Entity e : list) {
            if (ents.size() >= 40) break;
            ents.add(entity(e, p));
        }
        o.addProperty("entity_radius", r);
        o.addProperty("entity_count", list.size());
        o.add("entities", ents);
        return o;
    }

    static JsonObject entity(Entity e, LocalPlayer p) {
        JsonObject o = new JsonObject();
        o.addProperty("id", e.getId());
        o.addProperty("type", String.valueOf(BuiltInRegistries.ENTITY_TYPE.getKey(e.getType())));
        o.addProperty("name", e.getName().getString());
        o.add("pos", J.vec(e.position()));
        if (p != null) o.addProperty("distance", J.round(e.distanceTo(p)));
        if (e instanceof LivingEntity le) {
            o.addProperty("health", J.round(le.getHealth()));
            o.addProperty("max_health", J.round(le.getMaxHealth()));
        }
        if (e instanceof ItemEntity ie) o.add("item", item(ie.getItem(), false));
        if (e.isVehicle() || e.isPassenger()) o.addProperty("riding", e.isPassenger());
        return o;
    }

    static JsonObject block(ClientLevel level, BlockPos pos, boolean detailed) {
        BlockState st = level.getBlockState(pos);
        JsonObject o = new JsonObject();
        o.addProperty("id", String.valueOf(BuiltInRegistries.BLOCK.getKey(st.getBlock())));
        o.addProperty("name", st.getBlock().getName().getString());
        if (!st.getValues().isEmpty()) {
            JsonObject props = new JsonObject();
            for (var e : st.getValues().entrySet()) props.addProperty(e.getKey().getName(), propName(e.getKey(), e.getValue()));
            o.add("properties", props);
        }
        if (!detailed) return o;
        o.add("pos", J.pos(pos));
        o.addProperty("air", st.isAir());
        o.addProperty("hardness", J.round(st.getDestroySpeed(level, pos)));
        o.addProperty("has_block_entity", level.getBlockEntity(pos) != null);
        o.addProperty("block_light", level.getBrightness(LightLayer.BLOCK, pos));
        o.addProperty("sky_light", level.getBrightness(LightLayer.SKY, pos));
        o.addProperty("loaded", level.isLoaded(pos));
        LocalPlayer p = J.mc().player;
        if (p != null) o.addProperty("distance", J.round(Vec3.atCenterOf(pos).distanceTo(p.getEyePosition())));
        return o;
    }

    @SuppressWarnings("unchecked")
    private static <T extends Comparable<T>> String propName(Property<T> prop, Comparable<?> value) {
        return prop.getName((T) value);
    }

    /** 在周围的立方体范围里查找方块，按距离返回 */
    static JsonObject findBlocks(String query, int radius, int limit) {
        Minecraft mc = J.mc();
        LocalPlayer p = J.player();
        String q = query.toLowerCase();
        int r = Math.max(1, Math.min(radius, 32));
        BlockPos c = p.blockPosition();
        List<BlockPos> found = new ArrayList<>();
        BlockPos.MutableBlockPos m = new BlockPos.MutableBlockPos();
        for (int x = -r; x <= r; x++)
            for (int y = -r; y <= r; y++)
                for (int z = -r; z <= r; z++) {
                    m.set(c.getX() + x, c.getY() + y, c.getZ() + z);
                    if (!mc.level.isLoaded(m)) continue;
                    BlockState st = mc.level.getBlockState(m);
                    if (st.isAir()) continue;
                    String id = String.valueOf(BuiltInRegistries.BLOCK.getKey(st.getBlock()));
                    if (q.contains(":") ? id.equals(q) : id.contains(q)) found.add(m.immutable());
                }
        found.sort(Comparator.comparingDouble(b -> b.distSqr(c)));
        JsonObject o = new JsonObject();
        o.addProperty("radius", r);
        o.addProperty("total", found.size());
        JsonArray arr = new JsonArray();
        for (BlockPos b : found) {
            if (arr.size() >= Math.max(1, limit)) break;
            JsonObject bo = J.pos(b);
            bo.addProperty("id", String.valueOf(BuiltInRegistries.BLOCK.getKey(mc.level.getBlockState(b).getBlock())));
            bo.addProperty("distance", J.round(Math.sqrt(b.distSqr(c))));
            arr.add(bo);
        }
        o.add("blocks", arr);
        return o;
    }
}
