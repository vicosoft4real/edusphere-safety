"use client";

import { useEffect, useState } from "react";

export function OfflineBanner() {
  const [online, setOnline] = useState(true);
  useEffect(() => {
    setOnline(navigator.onLine);
    const on = () => setOnline(true);
    const off = () => setOnline(false);
    window.addEventListener("online", on);
    window.addEventListener("offline", off);
    return () => {
      window.removeEventListener("online", on);
      window.removeEventListener("offline", off);
    };
  }, []);
  if (online) return null;
  return (
    <p role="alert" className="bg-amber-200 px-4 py-2 text-center text-sm font-medium text-amber-900">
      You are offline. Shown data may be stale; actions will fail until connectivity returns.
    </p>
  );
}
