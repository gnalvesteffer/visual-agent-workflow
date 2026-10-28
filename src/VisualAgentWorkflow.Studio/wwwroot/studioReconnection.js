(() => {
    const styleText = `
        .components-reconnect-overlay {
            position: fixed;
            inset: auto 1rem 1rem auto;
            z-index: 10000;
            display: none;
            overflow: visible;
            pointer-events: none;
        }

        .components-reconnect-overlay.components-reconnect-visible { display: block; }
        .components-reconnect-overlay::before { display: none; }
        .components-reconnect-dialog {
            position: relative;
            display: grid;
            grid-template-columns: 2.5rem minmax(0, 1fr);
            grid-template-areas: "animation status" "animation action";
            align-items: center;
            gap: .35rem .75rem;
            inline-size: min(24rem, calc(100vw - 2rem));
            margin: 0;
            padding: .9rem 1rem;
            border: 1px solid var(--vaw-color-accent, #c4b5fd);
            border-radius: .65rem;
            background: var(--vaw-color-surface, #20232b);
            color: var(--vaw-color-text, #eef0f6);
            box-shadow: 0 .75rem 2rem #0008;
            opacity: 1;
            animation: none;
            pointer-events: auto;
        }
        .components-reconnect-overlay p {
            grid-area: status;
            margin: 0;
            color: var(--vaw-color-text, #eef0f6);
            font: 600 .82rem/1.35 var(--vaw-font-family, "Segoe UI", sans-serif);
            text-align: start;
        }
        .components-rejoining-animation {
            grid-area: animation;
            position: relative;
            inline-size: 2.25rem;
            block-size: 2.25rem;
        }
        .components-rejoining-animation div {
            box-sizing: border-box;
            border-color: var(--vaw-color-accent, #c4b5fd);
            animation-name: vaw-rejoining-ripple;
        }
        @keyframes vaw-rejoining-ripple {
            0%, 4.9% {
                top: 50%;
                left: 50%;
                width: 0;
                height: 0;
                opacity: 0;
            }
            5% {
                top: 50%;
                left: 50%;
                width: 0;
                height: 0;
                opacity: 1;
            }
            100% {
                top: 0;
                left: 0;
                width: 100%;
                height: 100%;
                opacity: 0;
            }
        }
        .components-reconnect-overlay button {
            grid-area: action;
            justify-self: start;
            min-block-size: 1.8rem;
            border: 1px solid var(--vaw-color-control-border, #737e93);
            border-radius: .35rem;
            padding: .25rem .65rem;
            background: var(--vaw-color-control, #2b303b);
            color: var(--vaw-color-text, #eef0f6);
            font: 600 .75rem/1.2 var(--vaw-font-family, "Segoe UI", sans-serif);
            cursor: pointer;
        }
        .components-reconnect-overlay button:hover {
            border-color: var(--vaw-color-focus, #d3c5ff);
            background: color-mix(in srgb, var(--vaw-color-control, #2b303b) 80%, var(--vaw-color-accent, #c4b5fd));
        }
        .components-reconnect-overlay button:focus-visible {
            outline: 2px solid var(--vaw-color-focus, #d3c5ff);
            outline-offset: 2px;
        }
        @media (max-width: 42rem) {
            .components-reconnect-overlay { inset-inline: .75rem; inset-block-end: .75rem; }
            .components-reconnect-dialog { inline-size: 100%; }
        }
    `;

    function install(host) {
        const shadow = host?.shadowRoot;
        if (!shadow || shadow.querySelector("style[data-vaw-reconnect]") !== null) return;
        const style = document.createElement("style");
        style.dataset.vawReconnect = "true";
        style.textContent = styleText;
        shadow.appendChild(style);
    }

    function scan() { install(document.getElementById("components-reconnect-modal")); }
    new MutationObserver(scan).observe(document.documentElement, { childList: true, subtree: true });
    scan();
})();
