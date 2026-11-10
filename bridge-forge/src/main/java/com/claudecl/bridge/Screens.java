package com.claudecl.bridge;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.components.AbstractButton;
import net.minecraft.client.gui.components.AbstractSliderButton;
import net.minecraft.client.gui.components.AbstractWidget;
import net.minecraft.client.gui.components.Checkbox;
import net.minecraft.client.gui.components.EditBox;
import net.minecraft.client.gui.components.events.ContainerEventHandler;
import net.minecraft.client.gui.components.events.GuiEventListener;
import net.minecraft.client.gui.screens.Screen;
import net.minecraft.client.gui.screens.inventory.AbstractContainerScreen;

import java.util.ArrayList;
import java.util.List;

/**
 * 界面操作：查看当前界面上的按钮和输入框，点击、输入文字、关闭界面
 */
final class Screens {
    private Screens() {
    }

    static JsonObject brief(Screen s) {
        JsonObject o = new JsonObject();
        o.addProperty("class", s.getClass().getSimpleName());
        o.addProperty("title", s.getTitle().getString());
        o.addProperty("pauses_game", s.isPauseScreen());
        if (s instanceof AbstractContainerScreen<?>) o.addProperty("is_container", true);
        return o;
    }

    /** 按深度优先收集界面上所有控件，编号就是列表里的位置 */
    private static List<AbstractWidget> widgets(Screen s) {
        List<AbstractWidget> out = new ArrayList<>();
        collect(s, out, 0);
        return out;
    }

    private static void collect(ContainerEventHandler parent, List<AbstractWidget> out, int depth) {
        if (depth > 6) return;
        for (GuiEventListener c : parent.children()) {
            if (c instanceof AbstractWidget w) out.add(w);
            if (c instanceof ContainerEventHandler sub && c != parent) collect(sub, out, depth + 1);
        }
    }

    private static String kind(AbstractWidget w) {
        if (w instanceof EditBox) return "text_box";
        if (w instanceof Checkbox) return "checkbox";
        if (w instanceof AbstractSliderButton) return "slider";
        if (w instanceof AbstractButton) return "button";
        return "widget";
    }

    static JsonObject describe() {
        Minecraft mc = J.mc();
        Screen s = mc.screen;
        JsonObject o = new JsonObject();
        if (s == null) {
            o.addProperty("screen", "none");
            o.addProperty("hint", mc.player != null ? "没有打开界面，正在游戏画面中" : "没有打开界面");
            return o;
        }
        o.add("screen", brief(s));
        o.addProperty("width", s.width);
        o.addProperty("height", s.height);
        JsonArray arr = new JsonArray();
        List<AbstractWidget> list = widgets(s);
        for (int i = 0; i < list.size(); i++) {
            AbstractWidget w = list.get(i);
            JsonObject wo = new JsonObject();
            wo.addProperty("index", i);
            wo.addProperty("kind", kind(w));
            wo.addProperty("label", w.getMessage().getString());
            wo.addProperty("active", w.active);
            wo.addProperty("visible", w.visible);
            wo.addProperty("x", w.getX());
            wo.addProperty("y", w.getY());
            wo.addProperty("w", w.getWidth());
            wo.addProperty("h", w.getHeight());
            if (w instanceof EditBox e) wo.addProperty("value", e.getValue());
            if (w instanceof Checkbox c) wo.addProperty("checked", c.selected());
            arr.add(wo);
        }
        o.add("widgets", arr);
        if (s instanceof AbstractContainerScreen<?>) o.addProperty("hint", "这是物品容器界面，用 get_container 查看格子，用 container_click 移动物品");
        return o;
    }

    private static AbstractWidget find(Screen s, JsonObject a) {
        List<AbstractWidget> list = widgets(s);
        if (J.has(a, "index")) {
            int i = J.integer(a, "index", -1);
            if (i < 0 || i >= list.size()) throw new BridgeException("没有编号为 " + i + " 的控件（共 " + list.size() + " 个）");
            return list.get(i);
        }
        String label = J.str(a, "label", null);
        if (label == null) throw new BridgeException("需要 index 或 label 参数");
        String want = label.trim().toLowerCase();
        // 先找文字完全一样的，再找包含这段文字的；优先可以点击的
        for (int pass = 0; pass < 2; pass++)
            for (AbstractWidget w : list) {
                String t = w.getMessage().getString().trim().toLowerCase();
                boolean hit = pass == 0 ? t.equals(want) : t.contains(want);
                if (hit && w.visible && w.active) return w;
            }
        for (AbstractWidget w : list)
            if (w.getMessage().getString().toLowerCase().contains(want))
                throw new BridgeException("控件「" + w.getMessage().getString() + "」现在不能点击");
        throw new BridgeException("当前界面上没有文字为「" + label + "」的控件，可以先用 get_screen 查看");
    }

    static JsonObject click(JsonObject a) {
        Screen s = needScreen();
        AbstractWidget w = find(s, a);
        double cx = w.getX() + w.getWidth() / 2.0;
        double cy = w.getY() + w.getHeight() / 2.0;
        // 通过界面分发点击，和真实鼠标点击走同一条路径
        s.mouseClicked(cx, cy, 0);
        s.mouseReleased(cx, cy, 0);
        JsonObject o = new JsonObject();
        o.addProperty("clicked", w.getMessage().getString());
        o.add("now", after());
        return o;
    }

    static JsonObject clickAt(double x, double y, int button) {
        Screen s = needScreen();
        s.mouseClicked(x, y, button);
        s.mouseReleased(x, y, button);
        JsonObject o = new JsonObject();
        o.add("now", after());
        return o;
    }

    static JsonObject type(JsonObject a) {
        Screen s = needScreen();
        String text = J.str(a, "text", "");
        EditBox box = null;
        if (J.has(a, "index") || J.has(a, "label")) {
            if (find(s, a) instanceof EditBox e) box = e;
            else throw new BridgeException("这个控件不是输入框");
        } else {
            if (s.getFocused() instanceof EditBox e) box = e;
            else for (AbstractWidget w : widgets(s)) if (w instanceof EditBox e && e.visible) { box = e; break; }
        }
        if (box == null) throw new BridgeException("当前界面上没有输入框");
        s.setFocused(box);
        if (J.bool(a, "replace", true)) box.setValue(text);
        else box.insertText(text);
        JsonObject o = new JsonObject();
        o.addProperty("value", box.getValue());
        return o;
    }

    static JsonObject close() {
        Minecraft mc = J.mc();
        Screen s = mc.screen;
        JsonObject o = new JsonObject();
        if (s == null) {
            o.addProperty("closed", "none");
            return o;
        }
        o.addProperty("closed", s.getTitle().getString());
        s.onClose();
        o.add("now", after());
        return o;
    }

    static Screen needScreen() {
        Screen s = J.mc().screen;
        if (s == null) throw new BridgeException("现在没有打开任何界面");
        return s;
    }

    private static JsonObject after() {
        Screen s = J.mc().screen;
        if (s == null) {
            JsonObject o = new JsonObject();
            o.addProperty("screen", "none");
            return o;
        }
        return brief(s);
    }
}
