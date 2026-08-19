package com.codex.bf66display;

import android.app.Activity;
import android.content.SharedPreferences;
import android.content.pm.ActivityInfo;
import android.graphics.Color;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.View;
import android.view.WindowManager;
import android.webkit.JavascriptInterface;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.net.URL;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Enumeration;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.concurrent.CompletionService;
import java.util.concurrent.ExecutorCompletionService;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;

public class MainActivity extends Activity {
    private static final String USB_BASE = "http://127.0.0.1:8787";
    private static final String PREFS = "bf66_connection";
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService connectionExecutor = Executors.newSingleThreadExecutor();
    private WebView webView;
    private volatile boolean destroyed;
    private volatile String currentBase = "";
    private String token = "";

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        enterImmersiveMode();
        loadConnectionToken();

        webView = new WebView(this);
        webView.setBackgroundColor(Color.rgb(8, 21, 37));
        WebSettings settings = webView.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setCacheMode(WebSettings.LOAD_NO_CACHE);
        settings.setMediaPlaybackRequiresUserGesture(false);
        webView.addJavascriptInterface(new OrientationBridge(), "BF66");
        webView.setWebViewClient(new WebViewClient() {
            @Override
            public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                super.onReceivedError(view, request, error);
                if (request.isForMainFrame()) {
                    currentBase = "";
                    showWaitingPage();
                }
            }
        });
        setContentView(webView);
        showWaitingPage();
        scheduleConnectionCheck(100);
    }

    private void loadConnectionToken() {
        SharedPreferences preferences = getSharedPreferences(PREFS, MODE_PRIVATE);
        String supplied = getIntent().getStringExtra("token");
        if (validToken(supplied)) {
            token = supplied.toUpperCase(Locale.ROOT);
            preferences.edit().putString("token", token).apply();
        } else {
            String saved = preferences.getString("token", "");
            if (validToken(saved)) token = saved.toUpperCase(Locale.ROOT);
        }
    }

    private static boolean validToken(String value) {
        return value != null && value.matches("[0-9a-fA-F]{48}");
    }

    private void scheduleConnectionCheck(long delayMs) {
        if (destroyed) return;
        handler.postDelayed(() -> {
            if (!destroyed) connectionExecutor.submit(this::checkConnection);
        }, delayMs);
    }

    private void checkConnection() {
        if (destroyed) return;
        String found = "";
        if (!token.isEmpty() && healthy(USB_BASE, 250)) found = USB_BASE;
        if (found.isEmpty() && !token.isEmpty()) found = discoverWirelessHost();

        if (!found.isEmpty()) {
            if (!found.equals(currentBase)) {
                currentBase = found;
                String url = found + "/display?token=" + token;
                handler.post(() -> {
                    if (!destroyed) webView.loadUrl(url);
                });
            }
        } else if (!currentBase.isEmpty()) {
            currentBase = "";
            handler.post(this::showWaitingPage);
        }
        scheduleConnectionCheck(found.isEmpty() ? 1800 : 1200);
    }

    private boolean healthy(String base, int timeoutMs) {
        HttpURLConnection connection = null;
        try {
            connection = (HttpURLConnection) new URL(base + "/health?token=" + token).openConnection();
            connection.setConnectTimeout(timeoutMs);
            connection.setReadTimeout(Math.max(450, timeoutMs));
            connection.setUseCaches(false);
            if (connection.getResponseCode() != 200) return false;
            try (BufferedReader reader = new BufferedReader(new InputStreamReader(connection.getInputStream()))) {
                return "ok".equals(reader.readLine());
            }
        } catch (Exception ignored) {
            return false;
        } finally {
            if (connection != null) connection.disconnect();
        }
    }

    private String discoverWirelessHost() {
        List<String> wifiPrefixes = new ArrayList<>();
        Set<String> seen = new HashSet<>();
        try {
            Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
            for (NetworkInterface network : Collections.list(interfaces)) {
                if (!network.isUp() || network.isLoopback()) continue;
                String name = network.getName().toLowerCase(Locale.ROOT);
                if (!name.contains("wlan") && !name.contains("wifi")) continue;
                for (InetAddress address : Collections.list(network.getInetAddresses())) {
                    if (!(address instanceof Inet4Address) || address.isLoopbackAddress()) continue;
                    byte[] bytes = address.getAddress();
                    if (!isPrivate(bytes)) continue;
                    String prefix = (bytes[0] & 255) + "." + (bytes[1] & 255) + "." + (bytes[2] & 255) + ".";
                    if (!seen.add(prefix)) continue;
                    wifiPrefixes.add(prefix);
                }
            }
        } catch (Exception ignored) { }
        return scanPrefixes(wifiPrefixes);
    }

    private String scanPrefixes(List<String> prefixes) {
        if (prefixes.isEmpty()) return "";
        List<String> candidates = new ArrayList<>();
        int[] priority = { 2, 1, 3, 254 };
        for (String prefix : prefixes) {
            for (int host : priority) candidates.add(prefix + host);
            for (int host = 4; host <= 253; host++) candidates.add(prefix + host);
        }

        ExecutorService pool = Executors.newFixedThreadPool(28);
        CompletionService<String> completion = new ExecutorCompletionService<>(pool);
        for (String address : candidates) {
            completion.submit(() -> {
                String base = "http://" + address + ":8787";
                return healthy(base, 260) ? base : "";
            });
        }

        long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(3800);
        try {
            for (int i = 0; i < candidates.size(); i++) {
                long remaining = deadline - System.nanoTime();
                if (remaining <= 0) break;
                Future<String> result = completion.poll(remaining, TimeUnit.NANOSECONDS);
                if (result == null) break;
                String found = result.get();
                if (!found.isEmpty()) return found;
            }
        } catch (Exception ignored) {
        } finally {
            pool.shutdownNow();
        }
        return "";
    }

    private static boolean isPrivate(byte[] bytes) {
        int first = bytes[0] & 255;
        int second = bytes[1] & 255;
        return first == 10 || (first == 172 && second >= 16 && second <= 31) || (first == 192 && second == 168);
    }

    private final class OrientationBridge {
        @JavascriptInterface
        public void setOrientation(String orientation) {
            runOnUiThread(() -> {
                int requested = "landscape".equals(orientation)
                    ? ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
                    : ActivityInfo.SCREEN_ORIENTATION_PORTRAIT;
                if (getRequestedOrientation() != requested) setRequestedOrientation(requested);
            });
        }
    }

    private void showWaitingPage() {
        if (destroyed || webView == null) return;
        String detail = token.isEmpty()
            ? "请先使用 USB 连接电脑完成一次配对"
            : "正在寻找 USB 或同一 Wi-Fi 下的电脑…";
        String html = "<html><meta name='viewport' content='width=device-width,initial-scale=1'><body style='margin:0;background:#081525;color:#f4f8ff;font-family:sans-serif;display:flex;height:100vh;align-items:center;justify-content:center;text-align:center'><div><div style='font-size:28px;font-weight:700'>BF66 显示屏</div><div style='font-size:16px;opacity:.72;margin-top:14px'>请在电脑上打开 BF66 显示控制台<br>" + detail + "</div></div></body></html>";
        webView.loadDataWithBaseURL(null, html, "text/html", "UTF-8", null);
    }

    private void enterImmersiveMode() {
        getWindow().getDecorView().setSystemUiVisibility(
            View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY |
            View.SYSTEM_UI_FLAG_FULLSCREEN |
            View.SYSTEM_UI_FLAG_HIDE_NAVIGATION |
            View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN |
            View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION |
            View.SYSTEM_UI_FLAG_LAYOUT_STABLE);
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) enterImmersiveMode();
    }

    @Override
    protected void onDestroy() {
        destroyed = true;
        handler.removeCallbacksAndMessages(null);
        connectionExecutor.shutdownNow();
        if (webView != null) webView.destroy();
        super.onDestroy();
    }
}
