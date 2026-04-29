document.addEventListener("DOMContentLoaded", () => {
    const masthead = document.querySelector(".masthead");
    const toggle = document.querySelector(".menu-toggle");

    if (!masthead || !toggle) {
        return;
    }

    toggle.addEventListener("click", () => {
        const isOpen = masthead.classList.toggle("menu-open");
        toggle.setAttribute("aria-expanded", String(isOpen));
    });
});
