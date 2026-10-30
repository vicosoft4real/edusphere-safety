import "./globals.css";
import type { Metadata } from "next";
import { OfflineBanner } from "./offline-banner";
import { Providers } from "./providers";

export const metadata: Metadata = {
  title: "EduSphere Safety",
  description: "CCTV event detection and incident response for schools",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body className="bg-stone-100 text-stone-900 antialiased">
        <Providers>
          <OfflineBanner />
          {children}
        </Providers>
      </body>
    </html>
  );
}
