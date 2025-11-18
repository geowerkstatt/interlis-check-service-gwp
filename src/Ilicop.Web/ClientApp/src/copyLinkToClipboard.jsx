import { useState } from "react";
import { useTranslation } from "react-i18next";

export const CopyLinkToClipboard = ({ tooltipText, link, children, className }) => {
  const [wasCopied, setWasCopied] = useState(false);
  const { t } = useTranslation();

  const copyToClipboard = () => {
    navigator.clipboard.writeText(new URL(link, window.location).href);
    setWasCopied(true);
  };

  return (
    <div onClick={copyToClipboard} onMouseLeave={() => setWasCopied(false)} className={className}>
      {children}
      <span className="icon-tooltip-text">{wasCopied ? t("copyLinkToClipboard.linkCopied") : tooltipText}</span>
    </div>
  );
};
