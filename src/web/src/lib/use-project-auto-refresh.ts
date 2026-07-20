"use client";

import { useEffect } from "react";
import { streamProjectEvents } from "@/lib/api";

export function useProjectAutoRefresh(
  projectId: string,
  getToken: () => Promise<string | null>,
  onRefresh: () => Promise<void>,
  enabled: boolean,
) {
  useEffect(() => {
    if (!enabled) return;
    const cancellation = new AbortController();
    let refreshTimer: number | undefined;
    let refreshing = false;
    let refreshPending = false;

    const refresh = async () => {
      if (refreshing) {
        refreshPending = true;
        return;
      }
      refreshing = true;
      try {
        await onRefresh();
      } catch {
        // The owning screen keeps its current snapshot; polling will retry.
      } finally {
        refreshing = false;
        if (refreshPending && !cancellation.signal.aborted) {
          refreshPending = false;
          void refresh();
        }
      }
    };
    const scheduleRefresh = () => {
      window.clearTimeout(refreshTimer);
      refreshTimer = window.setTimeout(() => void refresh(), 200);
    };

    void getToken().then((token) => {
      if (!token || cancellation.signal.aborted) return;
      void streamProjectEvents(
        projectId,
        token,
        scheduleRefresh,
        cancellation.signal,
      ).catch(() => {
        // Polling below is the fallback for proxies that buffer or reject SSE.
      });
    });
    const poll = window.setInterval(scheduleRefresh, 5_000);

    return () => {
      cancellation.abort();
      window.clearInterval(poll);
      window.clearTimeout(refreshTimer);
    };
  }, [enabled, getToken, onRefresh, projectId]);
}
