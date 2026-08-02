export function StatusPanel({
  title,
  message,
  tone = "neutral",
}: {
  title: string;
  message: string;
  tone?: "neutral" | "error" | "success";
}) {
  const tones = {
    neutral: {
      label: "Update",
      color: "var(--violet)",
    },
    error: {
      label: "Needs attention",
      color: "var(--danger)",
    },
    success: {
      label: "Ready",
      color: "var(--success)",
    },
  };
  const currentTone = tones[tone];

  return (
    <div
      className="surface-soft rounded-2xl border border-[var(--line)] p-5"
      role={tone === "error" ? "alert" : "status"}
      data-tone={tone}
    >
      <div className="flex flex-wrap items-center gap-3">
        <span className="status-chip surface-inset">
          <span
            className="size-1.5 rounded-full"
            style={{ backgroundColor: currentTone.color }}
            aria-hidden="true"
          />
          {currentTone.label}
        </span>
        <p className="font-black">{title}</p>
      </div>
      <p className="mt-2 text-sm leading-6 text-[var(--muted)]">{message}</p>
    </div>
  );
}
