package com.claudecl.bridge;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.mojang.blaze3d.platform.InputConstants;
import net.minecraft.client.KeyMapping;
import net.minecraft.client.Minecraft;
import net.minecraft.client.MouseHandler;
import net.minecraft.client.multiplayer.MultiPlayerGameMode;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.commands.arguments.EntityAnchorArgument;
import net.minecraft.core.BlockPos;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.network.chat.Component;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.InteractionResult;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.inventory.AbstractContainerMenu;
import net.minecraft.world.inventory.ClickType;
import net.minecraft.world.inventory.Slot;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.EntityHitResult;
import net.minecraft.world.phys.HitResult;
import net.minecraft.world.phys.Vec3;
import net.minecraftforge.client.ForgeHooksClient;
import net.minecraftforge.client.event.InputEvent;
import net.minecraftforge.common.ForgeHooks;

import java.lang.reflect.Field;
import java.lang.reflect.Modifier;

/**
 * 操作角色：视角、左键、右键、移动、切换物品、丢弃、容器格子、按键
 */
final class Actions {
    private Actions() {
    }

    // ---------- 视角 ----------

    /** 如果参数里带了目标（坐标或实体），先把视角转过去 */
    static void aimIfGiven(JsonObject a) {
        if (J.has(a, "x") || J.has(a, "entity_id") || J.has(a, "yaw")) look(a);
    }

    static JsonObject look(JsonObject a) {
        Minecraft mc = J.mc();
        LocalPlayer p = J.player();
        if (J.has(a, "entity_id")) {
            Entity e = mc.level.getEntity(J.integer(a, "entity_id", -1));
            if (e == null) throw new BridgeException("找不到这个实体，可能已经离开或死亡");
            face(p, e.getBoundingBox().getCenter());
        } else if (J.has(a, "x")) {
            double x = J.num(a, "x", 0), y = J.num(a, "y", p.getEyeY()), z = J.num(a, "z", 0);
            // 整数坐标当作方块，看向方块中心
            if (J.isWhole(a, "x") && J.isWhole(a, "y") && J.isWhole(a, "z")) {
                x += 0.5;
                y += 0.5;
                z += 0.5;
            }
            face(p, new Vec3(x, y, z));
        } else if (J.has(a, "yaw") || J.has(a, "pitch")) {
            float yaw = (float) J.num(a, "yaw", p.getYRot());
            float pitch = (float) Math.max(-90, Math.min(90, J.num(a, "pitch", p.getXRot())));
            p.setYRot(yaw);
            p.setXRot(pitch);
            p.yRotO = yaw;
            p.xRotO = pitch;
            p.setYHeadRot(yaw);
        } else {
            throw new BridgeException("需要 x/y/z、entity_id 或 yaw/pitch 中的一组参数");
        }
        JsonObject o = new JsonObject();
        o.addProperty("yaw", J.round(p.getYRot()));
        o.addProperty("pitch", J.round(p.getXRot()));
        o.add("looking_at", Info.lookingAt());
        return o;
    }

    private static void face(LocalPlayer p, Vec3 target) {
        p.lookAt(EntityAnchorArgument.Anchor.EYES, target);
        p.yRotO = p.getYRot();
        p.xRotO = p.getXRot();
        p.setYHeadRot(p.getYRot());
    }

    // ---------- 左键 ----------

    /** 鼠标是否被游戏捕获的开关：游戏只在捕获鼠标时才会持续挖掘，窗口不在前台时需要临时打开 */
    private static Field grabbedField;

    private static Field grabbedField(MouseHandler mh) {
        if (grabbedField != null) return grabbedField;
        // 逐个试探布尔字段，找到会改变“是否捕获鼠标”结果的那一个
        for (Field f : MouseHandler.class.getDeclaredFields()) {
            if (f.getType() != boolean.class || Modifier.isStatic(f.getModifiers())) continue;
            try {
                f.setAccessible(true);
                boolean orig = f.getBoolean(mh);
                boolean before = mh.isMouseGrabbed();
                f.setBoolean(mh, !orig);
                boolean changed = mh.isMouseGrabbed() != before;
                f.setBoolean(mh, orig);
                if (changed) return grabbedField = f;
            } catch (Exception ignored) {
            }
        }
        return null;
    }

    private static boolean setGrabbed(boolean value) {
        MouseHandler mh = J.mc().mouseHandler;
        Field f = grabbedField(mh);
        if (f == null) return false;
        try {
            boolean old = f.getBoolean(mh);
            f.setBoolean(mh, value);
            return old;
        } catch (Exception e) {
            return false;
        }
    }

    static JsonObject attack(JsonObject a) {
        int hold = J.integer(a, "hold_ticks", 0);
        BlockPos[] mining = new BlockPos[1];
        boolean[] oldGrab = new boolean[1];
        JsonObject r = J.main(() -> {
            J.player();
            J.needNoScreen();
            aimIfGiven(a);
            Minecraft mc = J.mc();
            LocalPlayer p = mc.player;
            MultiPlayerGameMode gm = mc.gameMode;
            mc.gameRenderer.pick(1.0F);
            HitResult hit = mc.hitResult;
            JsonObject o = new JsonObject();
            o.addProperty("attack_cooldown", J.round(p.getAttackStrengthScale(0.5F)));
            InputEvent.InteractionKeyMappingTriggered ev = ForgeHooksClient.onClickInput(0, mc.options.keyAttack, InteractionHand.MAIN_HAND);
            if (ev.isCanceled()) {
                if (ev.shouldSwingHand()) p.swing(InteractionHand.MAIN_HAND);
                o.addProperty("result", "cancelled");
                o.addProperty("detail", "这次左键被游戏里的其他逻辑拦截了");
                return o;
            }
            if (hit instanceof EntityHitResult eh) {
                Entity target = eh.getEntity();
                gm.attack(p, target);
                o.addProperty("result", "attacked_entity");
                o.add("target", Info.entity(target, p));
            } else if (hit instanceof BlockHitResult bh && hit.getType() == HitResult.Type.BLOCK && !mc.level.isEmptyBlock(bh.getBlockPos())) {
                BlockPos pos = bh.getBlockPos();
                o.add("target", Info.block(mc.level, pos, false));
                o.add("target_pos", J.pos(pos));
                // 先排除挖不动的情况，免得白白按住半分钟
                net.minecraft.world.level.block.state.BlockState bs = mc.level.getBlockState(pos);
                if (bs.getDestroySpeed(mc.level, pos) < 0 && !p.getAbilities().instabuild)
                    return fail(o, "这个方块无法破坏");
                if (p.getAbilities().instabuild && !p.getMainHandItem().getItem().canAttackBlock(bs, mc.level, pos, p))
                    return fail(o, "创造模式下拿着这个物品（例如剑）不能破坏方块，换别的物品或空手再试");
                gm.startDestroyBlock(pos, bh.getDirection());
                if (mc.level.getBlockState(pos).isAir()) {
                    o.addProperty("result", "broke_block");
                } else {
                    // 需要持续挖掘：按住左键，并让游戏认为鼠标在窗口里
                    o.addProperty("result", "mining");
                    mining[0] = pos;
                    mc.options.keyAttack.setDown(true);
                    oldGrab[0] = setGrabbed(true);
                }
            } else {
                p.resetAttackStrengthTicker();
                ForgeHooks.onEmptyLeftClick(p);
                o.addProperty("result", "missed");
                o.addProperty("detail", "准星没有对着方块或实体（或者距离太远）");
            }
            if (ev.shouldSwingHand()) p.swing(InteractionHand.MAIN_HAND);
            return o;
        });
        if (mining[0] == null) return r;

        // 挖掘：每刻检查方块是否已经碎了；hold_ticks 为 0 时一直挖到碎（最多 30 秒）
        BlockPos pos = mining[0];
        int limit = hold > 0 ? hold : 600;
        int ticks = 0;
        String end = "stopped";
        try {
            while (ticks < limit) {
                Ticks.await(1);
                ticks++;
                String state = J.main(() -> {
                    Minecraft mc = J.mc();
                    if (mc.player == null || mc.level == null) return "left_world";
                    if (mc.level.getBlockState(pos).isAir()) return "broke_block";
                    if (mc.screen != null) return "screen_opened";
                    return null;
                });
                if (state != null) {
                    end = state;
                    break;
                }
            }
        } finally {
            J.run(() -> {
                Minecraft mc = J.mc();
                mc.options.keyAttack.setDown(false);
                setGrabbed(oldGrab[0]);
                if (mc.gameMode != null && mc.level != null && !mc.level.getBlockState(pos).isAir()) mc.gameMode.stopDestroyBlock();
            });
        }
        r.addProperty("result", end.equals("stopped") ? "stopped_mining" : end);
        r.addProperty("ticks", ticks);
        if (end.equals("stopped")) r.addProperty("detail", "按住 " + ticks + " 刻后松开了，方块还没有挖掉");
        return r;
    }

    // ---------- 右键 ----------

    static JsonObject use(JsonObject a) {
        int hold = J.integer(a, "hold_ticks", 0);
        if (hold == 0) return J.main(() -> {
            J.player();
            J.needNoScreen();
            aimIfGiven(a);
            return useOnce();
        });

        // 按住右键：交给游戏自己的按键逻辑（拉弓、吃东西、举盾等），-1 表示一直按到用完为止
        JsonObject start = J.main(() -> {
            J.player();
            J.needNoScreen();
            aimIfGiven(a);
            JsonObject o = new JsonObject();
            o.add("item_before", Info.item(J.mc().player.getMainHandItem(), false));
            J.mc().options.keyUse.setDown(true);
            return o;
        });
        int limit = hold > 0 ? hold : 200;
        int ticks = 0;
        boolean sawUsing = false;
        int prevRemain = Integer.MAX_VALUE;
        String end = "released";
        try {
            while (ticks < limit) {
                Ticks.await(1);
                ticks++;
                // [是否正在使用, 剩余使用刻数]；玩家不在或界面打开时为 null
                int[] st = J.main(() -> {
                    Minecraft mc = J.mc();
                    if (mc.player == null || mc.screen != null) return null;
                    return new int[]{mc.player.isUsingItem() ? 1 : 0, mc.player.getUseItemRemainingTicks()};
                });
                if (st == null) {
                    end = "interrupted";
                    break;
                }
                boolean using = st[0] == 1;
                // 按键一直按着时，游戏会在用完的同一刻重新开始使用（例如接着吃下一个），
                // 所以“剩余刻数突然变大”也算这一次已经用完
                boolean restarted = using && sawUsing && st[1] > prevRemain;
                sawUsing |= using;
                prevRemain = using ? st[1] : Integer.MAX_VALUE;
                if (hold < 0 && sawUsing && (!using || restarted)) {
                    end = "finished_using";
                    break;
                }
                if (hold < 0 && !sawUsing && ticks >= 10) {
                    end = "not_a_held_item";
                    break;
                }
            }
        } finally {
            J.run(() -> J.mc().options.keyUse.setDown(false));
        }
        Ticks.await(2);
        JsonObject o = start;
        o.addProperty("result", end);
        o.addProperty("ticks", ticks);
        o.addProperty("was_using_item", sawUsing);
        J.run(() -> {
            if (J.mc().player != null) o.add("item_after", Info.item(J.mc().player.getMainHandItem(), false));
        });
        return o;
    }

    /** 一次右键：先对准星对着的实体或方块使用，没有效果时再使用手里的物品，主手不行再试副手 */
    private static JsonObject useOnce() {
        Minecraft mc = J.mc();
        LocalPlayer p = mc.player;
        MultiPlayerGameMode gm = mc.gameMode;
        JsonObject o = new JsonObject();
        if (gm.isDestroying()) throw new BridgeException("正在挖掘方块，不能同时右键");
        if (p.isHandsBusy()) throw new BridgeException("双手正忙（例如在划船）");
        mc.gameRenderer.pick(1.0F);
        HitResult hit = mc.hitResult;
        for (InteractionHand hand : InteractionHand.values()) {
            InputEvent.InteractionKeyMappingTriggered ev = ForgeHooksClient.onClickInput(1, mc.options.keyUse, hand);
            if (ev.isCanceled()) {
                if (ev.shouldSwingHand()) p.swing(hand);
                o.addProperty("result", "cancelled");
                return o;
            }
            ItemStack stack = p.getItemInHand(hand);
            o.addProperty("hand", hand == InteractionHand.MAIN_HAND ? "main_hand" : "off_hand");
            o.add("item", Info.item(stack, false));
            if (hit instanceof EntityHitResult eh) {
                Entity e = eh.getEntity();
                if (!mc.level.getWorldBorder().isWithinBounds(e.blockPosition())) return fail(o, "目标在世界边界外");
                InteractionResult res = gm.interactAt(p, e, eh, hand);
                if (!res.consumesAction()) res = gm.interact(p, e, hand);
                if (res.consumesAction()) {
                    if (res.shouldSwing() && ev.shouldSwingHand()) p.swing(hand);
                    o.addProperty("result", "used_on_entity");
                    o.add("target", Info.entity(e, p));
                    return o;
                }
            } else if (hit instanceof BlockHitResult bh && hit.getType() == HitResult.Type.BLOCK) {
                int count = stack.getCount();
                o.add("target", Info.block(mc.level, bh.getBlockPos(), false));
                o.add("target_pos", J.pos(bh.getBlockPos()));
                o.addProperty("face", bh.getDirection().getName());
                InteractionResult res = gm.useItemOn(p, hand, bh);
                if (res.consumesAction()) {
                    if (res.shouldSwing() && ev.shouldSwingHand()) p.swing(hand);
                    o.addProperty("result", "used_on_block");
                    o.addProperty("item_consumed", stack.getCount() != count);
                    return o;
                }
                if (res == InteractionResult.FAIL) {
                    // 放方块失败最常见的原因是放置位置和自己重叠
                    BlockPos place = bh.getBlockPos().relative(bh.getDirection());
                    if (stack.getItem() instanceof net.minecraft.world.item.BlockItem &&
                            p.getBoundingBox().intersects(new net.minecraft.world.phys.AABB(place)))
                        return fail(o, "要放方块的位置被自己挡住了（例如低头对着脚下），换个位置或先移动一下");
                    return fail(o, "对这个方块使用失败");
                }
            }
            if (stack.isEmpty() && (hit == null || hit.getType() == HitResult.Type.MISS)) ForgeHooks.onEmptyClick(p, hand);
            if (!stack.isEmpty()) {
                InteractionResult res = gm.useItem(p, hand);
                if (res.consumesAction()) {
                    if (res.shouldSwing()) p.swing(hand);
                    o.addProperty("result", "used_item");
                    o.addProperty("now_using", p.isUsingItem());
                    if (p.isUsingItem()) o.addProperty("detail", "这个物品需要按住使用（例如食物、弓），可以用 hold_ticks 参数按住");
                    return o;
                }
            }
        }
        o.remove("hand");
        o.remove("item");
        o.addProperty("result", "nothing_happened");
        o.addProperty("detail", "右键没有产生效果（手里的东西不能用在这里，或者没有对准目标）");
        return o;
    }

    private static JsonObject fail(JsonObject o, String why) {
        o.addProperty("result", "failed");
        o.addProperty("detail", why);
        return o;
    }

    // ---------- 物品栏 ----------

    static JsonObject selectSlot(int slot) {
        LocalPlayer p = J.player();
        if (slot < 0 || slot > 8) throw new BridgeException("快捷栏编号是 0~8");
        p.getInventory().selected = slot;
        JsonObject o = new JsonObject();
        o.addProperty("selected_slot", slot);
        o.add("main_hand", Info.item(p.getMainHandItem(), false));
        return o;
    }

    /** 背包格子编号转换成玩家背包界面里的格子编号 */
    private static int menuSlot(int inv) {
        if (inv >= 0 && inv <= 8) return 36 + inv;
        if (inv >= 9 && inv <= 35) return inv;
        if (inv >= 36 && inv <= 39) return 44 - inv;
        if (inv == 40) return 45;
        throw new BridgeException("背包格子编号是 0~40");
    }

    private static void needPlayerMenu(LocalPlayer p) {
        if (p.containerMenu != p.inventoryMenu) throw new BridgeException("现在打开着别的容器，先 close_screen，或者用 container_click 操作");
    }

    /** 把背包里某一格和快捷栏（或副手）交换 */
    static JsonObject swap(int slot, int hotbar) {
        Minecraft mc = J.mc();
        LocalPlayer p = J.player();
        needPlayerMenu(p);
        if (hotbar != 40 && (hotbar < 0 || hotbar > 8)) throw new BridgeException("hotbar 需要是 0~8，或者 40 表示副手");
        mc.gameMode.handleInventoryMouseClick(p.inventoryMenu.containerId, menuSlot(slot), hotbar, ClickType.SWAP, p);
        JsonObject o = new JsonObject();
        o.add("slot_" + slot, Info.item(p.getInventory().getItem(slot), false));
        o.add("slot_" + hotbar, Info.item(p.getInventory().getItem(hotbar), false));
        return o;
    }

    /** 拿起某种物品：在快捷栏就切过去，在背包里就换到当前快捷栏 */
    static JsonObject holdItem(String query) {
        LocalPlayer p = J.player();
        String q = query.toLowerCase();
        int found = -1;
        // 先找快捷栏（当前选中的格子优先），再找背包
        if (matches(p.getInventory().getItem(p.getInventory().selected), q)) found = p.getInventory().selected;
        for (int i = 0; i < 36 && found < 0; i++)
            if (matches(p.getInventory().getItem(i), q)) found = i;
        if (found < 0) throw new BridgeException("背包里没有「" + query + "」");
        if (found < 9) return selectSlot(found);
        return swap(found, p.getInventory().selected);
    }

    /** 物品是否符合查询：带冒号时按完整编号比较，否则编号或显示名称包含即可 */
    private static boolean matches(ItemStack s, String q) {
        if (s.isEmpty()) return false;
        String id = String.valueOf(BuiltInRegistries.ITEM.getKey(s.getItem()));
        if (q.contains(":")) return id.equals(q);
        return id.contains(q) || s.getHoverName().getString().toLowerCase().contains(q);
    }

    static JsonObject drop(boolean all, int slot) {
        Minecraft mc = J.mc();
        LocalPlayer p = J.player();
        JsonObject o = new JsonObject();
        if (slot < 0 || slot == p.getInventory().selected) {
            o.add("dropped", Info.item(p.getMainHandItem(), false));
            if (p.drop(all)) p.swing(InteractionHand.MAIN_HAND);
        } else {
            needPlayerMenu(p);
            o.add("dropped", Info.item(p.getInventory().getItem(slot), false));
            mc.gameMode.handleInventoryMouseClick(p.inventoryMenu.containerId, menuSlot(slot), all ? 1 : 0, ClickType.THROW, p);
        }
        return o;
    }

    // ---------- 容器 ----------

    static JsonObject container() {
        LocalPlayer p = J.player();
        AbstractContainerMenu menu = p.containerMenu;
        JsonObject o = new JsonObject();
        o.addProperty("container_id", menu.containerId);
        o.addProperty("is_player_inventory", menu == p.inventoryMenu);
        try {
            o.addProperty("menu_type", String.valueOf(BuiltInRegistries.MENU.getKey(menu.getType())));
        } catch (Exception ignored) {
            o.addProperty("menu_type", "player_inventory");
        }
        if (J.mc().screen != null) o.addProperty("title", J.mc().screen.getTitle().getString());
        JsonArray arr = new JsonArray();
        for (Slot s : menu.slots) {
            ItemStack st = s.getItem();
            if (st.isEmpty()) continue;
            JsonObject so = Info.item(st, false);
            so.addProperty("slot", s.index);
            so.addProperty("in", s.container == p.getInventory() ? "player" : "container");
            if (s.container == p.getInventory()) so.addProperty("inventory_slot", s.getContainerSlot());
            arr.add(so);
        }
        o.addProperty("slot_count", menu.slots.size());
        o.add("items", arr);
        if (!menu.getCarried().isEmpty()) o.add("carried", Info.item(menu.getCarried(), false));
        o.addProperty("hint", "slot 是这个界面里的格子编号，container_click 用的就是它；空格子没有列出");
        return o;
    }

    static JsonObject containerClick(int slot, int button, String mode) {
        Minecraft mc = J.mc();
        LocalPlayer p = J.player();
        AbstractContainerMenu menu = p.containerMenu;
        ClickType type = switch (mode.toLowerCase()) {
            case "pickup", "click" -> ClickType.PICKUP;
            case "quick_move", "shift" -> ClickType.QUICK_MOVE;
            case "swap" -> ClickType.SWAP;
            case "throw" -> ClickType.THROW;
            case "pickup_all", "double" -> ClickType.PICKUP_ALL;
            case "clone" -> ClickType.CLONE;
            default -> throw new BridgeException("mode 只能是 pickup、quick_move、swap、throw、pickup_all、clone");
        };
        if (slot != -999 && (slot < 0 || slot >= menu.slots.size())) throw new BridgeException("这个界面只有 0~" + (menu.slots.size() - 1) + " 号格子（-999 表示界面外）");
        mc.gameMode.handleInventoryMouseClick(menu.containerId, slot, button, type, p);
        JsonObject o = new JsonObject();
        if (slot >= 0) o.add("slot_now", Info.item(menu.getSlot(slot).getItem(), false));
        o.add("carried", Info.item(menu.getCarried(), false));
        return o;
    }

    // ---------- 移动 ----------

    static KeyMapping dirKey(String d) {
        var o = J.mc().options;
        return switch (d.toLowerCase()) {
            case "forward", "w" -> o.keyUp;
            case "back", "backward", "s" -> o.keyDown;
            case "left", "a" -> o.keyLeft;
            case "right", "d" -> o.keyRight;
            case "none", "" -> null;
            default -> throw new BridgeException("direction 只能是 forward、back、left、right、none");
        };
    }

    static JsonObject move(JsonObject a) {
        String dir = J.str(a, "direction", "forward");
        int ticks = Math.max(1, Math.min(J.integer(a, "ticks", 20), 1200));
        boolean jump = J.bool(a, "jump", false), sneak = J.bool(a, "sneak", false), sprint = J.bool(a, "sprint", false);
        Vec3[] start = new Vec3[1];
        J.run(() -> {
            LocalPlayer p = J.player();
            J.needNoScreen();
            start[0] = p.position();
            KeyMapping k = dirKey(dir);
            if (k != null) k.setDown(true);
            var o = J.mc().options;
            if (jump) o.keyJump.setDown(true);
            if (sneak) o.keyShift.setDown(true);
            if (sprint) o.keySprint.setDown(true);
        });
        try {
            Ticks.await(ticks);
        } finally {
            J.run(Actions::releaseMoveKeys);
        }
        Ticks.await(1);
        return J.main(() -> {
            LocalPlayer p = J.player();
            JsonObject o = new JsonObject();
            o.add("from", J.vec(start[0]));
            o.add("to", J.vec(p.position()));
            o.addProperty("moved", J.round(p.position().distanceTo(start[0])));
            o.addProperty("on_ground", p.onGround());
            return o;
        });
    }

    static void releaseMoveKeys() {
        var o = J.mc().options;
        for (KeyMapping k : new KeyMapping[]{o.keyUp, o.keyDown, o.keyLeft, o.keyRight, o.keyJump, o.keyShift, o.keySprint})
            k.setDown(false);
    }

    /** 沿直线走到目标点：每刻转向目标、按住前进，被挡住时自动跳；不会绕路 */
    static JsonObject moveTo(JsonObject a) {
        double tx = J.num(a, "x", Double.NaN), tz = J.num(a, "z", Double.NaN);
        if (Double.isNaN(tx) || Double.isNaN(tz)) throw new BridgeException("需要 x 和 z");
        if (J.isWhole(a, "x")) tx += 0.5;
        if (J.isWhole(a, "z")) tz += 0.5;
        double fx = tx, fz = tz;
        double stopAt = Math.max(0.3, J.num(a, "distance", 0.6));
        int timeout = Math.max(20, Math.min(J.integer(a, "timeout_ticks", 600), 6000));
        boolean sprint = J.bool(a, "sprint", false);
        Vec3[] start = new Vec3[1];
        J.run(() -> {
            J.player();
            J.needNoScreen();
            start[0] = J.mc().player.position();
        });
        int ticks = 0, stuck = 0;
        double best = Double.MAX_VALUE;
        String end = "timeout";
        try {
            while (ticks < timeout) {
                String state = J.main(() -> {
                    Minecraft mc = J.mc();
                    LocalPlayer p = mc.player;
                    if (p == null) return "left_world";
                    if (mc.screen != null) return "screen_opened";
                    double dx = fx - p.getX(), dz = fz - p.getZ();
                    double dist = Math.sqrt(dx * dx + dz * dz);
                    if (dist <= stopAt) return "arrived";
                    float yaw = (float) (Math.toDegrees(Math.atan2(dz, dx)) - 90.0);
                    p.setYRot(yaw);
                    p.setYHeadRot(yaw);
                    var o = mc.options;
                    o.keyUp.setDown(true);
                    o.keySprint.setDown(sprint);
                    // 撞墙或者在水里时按住跳
                    o.keyJump.setDown(p.horizontalCollision || p.isInWater());
                    return "d" + dist;
                });
                if (!state.startsWith("d")) {
                    end = state;
                    break;
                }
                double dist = Double.parseDouble(state.substring(1));
                if (dist < best - 0.05) {
                    best = dist;
                    stuck = 0;
                } else if (++stuck > 60) {
                    end = "stuck";
                    break;
                }
                Ticks.await(1);
                ticks++;
            }
        } finally {
            J.run(Actions::releaseMoveKeys);
        }
        String result = end;
        int used = ticks;
        return J.main(() -> {
            LocalPlayer p = J.player();
            JsonObject o = new JsonObject();
            o.addProperty("result", result);
            if (result.equals("stuck")) o.addProperty("detail", "3 秒没有靠近目标，可能被挡住了；这个操作只会走直线，不会绕路");
            o.add("from", J.vec(start[0]));
            o.add("to", J.vec(p.position()));
            o.addProperty("remaining", J.round(Math.sqrt(Math.pow(fx - p.getX(), 2) + Math.pow(fz - p.getZ(), 2))));
            o.addProperty("ticks", used);
            return o;
        });
    }

    // ---------- 按键 ----------

    static InputConstants.Key parseKey(String name) {
        String n = name.trim().toLowerCase();
        if (!n.startsWith("key.")) {
            n = switch (n) {
                case "esc" -> "escape";
                case "return" -> "enter";
                case "shift", "lshift" -> "left.shift";
                case "ctrl", "control", "lctrl" -> "left.control";
                case "alt", "lalt" -> "left.alt";
                case "space", "spacebar" -> "space";
                default -> n;
            };
            n = n.startsWith("mouse.") ? "key." + n : "key.keyboard." + n;
        }
        try {
            return InputConstants.getKey(n);
        } catch (Exception e) {
            throw new BridgeException("不认识的按键：" + name + "（例如 e、enter、escape、f5、left.shift、mouse.left）");
        }
    }

    static JsonObject pressKey(JsonObject a) {
        Minecraft mc = J.mc();
        int hold = Math.max(1, Math.min(J.integer(a, "hold_ticks", 1), 1200));
        KeyMapping[] targets;
        InputConstants.Key key;
        if (J.has(a, "mapping")) {
            String m = J.need(a, "mapping");
            KeyMapping found = null;
            for (KeyMapping km : mc.options.keyMappings) {
                String tr = Component.translatable(km.getName()).getString();
                if (km.getName().equalsIgnoreCase(m) || tr.equalsIgnoreCase(m)) {
                    found = km;
                    break;
                }
            }
            if (found == null) throw new BridgeException("找不到按键功能「" + m + "」，可以用 list_keys 查看");
            targets = new KeyMapping[]{found};
            key = found.getKey();
        } else {
            key = parseKey(J.need(a, "key"));
            if (mc.screen != null) {
                // 有界面时把按键交给界面（例如回车确认、Esc 返回）
                int code = key.getValue();
                mc.screen.keyPressed(code, 0, 0);
                if (mc.screen != null) mc.screen.keyReleased(code, 0, 0);
                JsonObject o = new JsonObject();
                o.addProperty("sent_to", "screen");
                o.add("now", mc.screen == null ? null : Screens.brief(mc.screen));
                return o;
            }
            java.util.List<KeyMapping> list = new java.util.ArrayList<>();
            for (KeyMapping km : mc.options.keyMappings) if (km.getKey().equals(key)) list.add(km);
            if (list.isEmpty()) throw new BridgeException("按键 " + key.getName() + " 没有绑定任何功能");
            targets = list.toArray(new KeyMapping[0]);
        }
        // 先记一次点击，再按住指定的刻数后松开
        KeyMapping.click(key);
        for (KeyMapping km : targets) km.setDown(true);
        JsonObject o = new JsonObject();
        JsonArray names = new JsonArray();
        for (KeyMapping km : targets) names.add(km.getName());
        o.add("triggered", names);
        o.addProperty("hold_ticks", hold);
        o.add("_release_later", names);
        o.addProperty("_hold", hold);
        return o;
    }

    static void releaseMappings(JsonArray names) {
        Minecraft mc = J.mc();
        for (KeyMapping km : mc.options.keyMappings)
            for (var n : names) if (km.getName().equals(n.getAsString())) km.setDown(false);
    }

    static JsonObject listKeys() {
        Minecraft mc = J.mc();
        JsonArray arr = new JsonArray();
        for (KeyMapping km : mc.options.keyMappings) {
            JsonObject o = new JsonObject();
            o.addProperty("mapping", km.getName());
            o.addProperty("name", Component.translatable(km.getName()).getString());
            o.addProperty("category", Component.translatable(km.getCategory()).getString());
            o.addProperty("bound_to", km.getTranslatedKeyMessage().getString());
            arr.add(o);
        }
        JsonObject o = new JsonObject();
        o.add("keys", arr);
        return o;
    }

    static JsonObject respawn() {
        LocalPlayer p = J.player();
        if (!p.isDeadOrDying()) throw new BridgeException("玩家没有死亡，不需要重生");
        p.respawn();
        J.mc().setScreen(null);
        JsonObject o = new JsonObject();
        o.addProperty("result", "respawning");
        return o;
    }
}
