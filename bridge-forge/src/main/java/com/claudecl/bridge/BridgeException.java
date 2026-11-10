package com.claudecl.bridge;

/** 可以直接展示给调用方的错误（比如“还没有进入世界”） */
public final class BridgeException extends RuntimeException {
    public BridgeException(String message) {
        super(message);
    }
}
