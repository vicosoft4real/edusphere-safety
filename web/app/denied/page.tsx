import Link from "next/link";

export default function DeniedPage() {
  return (
    <main className="mx-auto mt-24 w-full max-w-sm px-4 text-center">
      <h1 className="text-2xl font-semibold">Not permitted</h1>
      <p className="mt-2 text-sm text-stone-600">
        Your account is not a member of this school tenant, or lacks access to that item. If you
        need access, ask a school administrator.
      </p>
      <div className="mt-4 space-x-4 text-sm">
        <Link href="/" className="underline">
          Overview
        </Link>
        <Link href="/login" className="underline">
          Switch user
        </Link>
      </div>
    </main>
  );
}
