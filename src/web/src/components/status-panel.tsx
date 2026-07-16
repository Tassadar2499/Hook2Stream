export function StatusPanel({
  title,
  message,
  tone = "neutral",
}: {
  title: string;
  message: string;
  tone?: "neutral" | "error" | "success";
}) {
  const colors = {
    neutral: "border-[var(--line)] bg-white/60",
    error: "border-red-700/30 bg-red-100 text-red-950",
    success: "border-green-800/30 bg-green-100 text-green-950",
  };

  return (
    <div className={`rounded-2xl border p-5 ${colors[tone]}`} role="status">
      <p className="font-black">{title}</p>
      <p className="mt-1 text-sm leading-6">{message}</p>
    </div>
  );
}
