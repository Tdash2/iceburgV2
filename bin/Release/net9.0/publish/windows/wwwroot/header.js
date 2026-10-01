(function () {
    function initializeHeader() {

        
    const style = document.createElement("style");

        style.textContent =
            "body {" +
            " margin: 0px;" +
            "  } " +
        ".iceburg-header {" +

           " position: sticky !important;"+
            "top: 0;"+
             "z - index: 1000;"+
            "height:64px;" +
            "background:#181818;" +
            "color:white;" +
            "padding:0 24px;" +
            "display:flex;" +
            "align-items:center;" +
            "width:100%;" +
            "font-family:Arial,Helvetica,sans-serif;" +
            "position:relative;" +
            "z-index:10000;" +
            "box-shadow:0 2px 8px rgba(0,0,0,0.25);" +
            "box-sizing:border-box;" +
        "}" +

        ".iceburg-logo-container {" +
            "display:flex;" +
            "flex-direction:column;" +
            "align-items:center;" +
            "justify-content:center;" +
            "height:100%;" +
            "flex-shrink:0;" +
            "cursor:pointer;" +
            "user-select:none;" +
        "}" +

        ".iceburg-logo-container img {" +
            "height:27px;" +
            "width:auto;" +
            "display:block;" +
            "object-fit:contain;" +
        "}" +

        ".iceburg-logo-text {" +
            "margin-top:3px;" +
            "font-size:18px;" +
            "font-weight:bold;" +
            "letter-spacing:1px;" +
            "color:white;" +
        "}" +

        ".iceburg-nav {" +
            "display:flex;" +
            "align-items:stretch;" +
            "height:100%;" +
            "margin-left:auto;" +
            "gap:4px;" +
        "}" +

        ".iceburg-nav-item {" +
            "position:relative;" +
            "display:flex;" +
            "align-items:center;" +
            "height:100%;" +
        "}" +

        ".iceburg-nav-link {" +
            "height:100%;" +
            "display:flex;" +
            "align-items:center;" +
            "gap:7px;" +
            "padding:0 16px;" +
            "color:white;" +
            "text-decoration:none;" +
            "font-size:14px;" +
            "white-space:nowrap;" +
            "cursor:pointer;" +
            "border:0;" +
            "background:transparent;" +
            "font-family:Arial,Helvetica,sans-serif;" +
    "box-sizing:border-box; " +
            "letter-spacing:1.2px"+
        "}" +

        ".iceburg-nav-link:hover {" +
            "background:#252525;" +
            "color:white;" +
        "}" +

        ".iceburg-nav-arrow {" +
            "font-size:9px;" +
            "opacity:0.7;" +
            "transition:transform 0.15s ease;" +
        "}" +

        ".iceburg-nav-item:hover > .iceburg-nav-link .iceburg-nav-arrow {" +
            "transform:rotate(180deg);" +
        "}" +

        ".iceburg-dropdown {" +
            "position:absolute;" +
            "top:100%;" +
            "right:0;" +
            "min-width:190px;" +
            "background:#202020;" +
            "border:1px solid #333;" +
            "border-radius:0 0 7px 7px;" +
            "box-shadow:0 8px 20px rgba(0,0,0,0.35);" +
            "padding:5px 0;" +
            "display:none;" +
            "z-index:10001;" +
            "box-sizing:border-box;" +
        "}" +

        ".iceburg-nav-item:hover > .iceburg-dropdown {" +
            "display:block;" +
        "}" +

        ".iceburg-dropdown .iceburg-nav-item {" +
            "height:auto;" +
            "width:100%;" +
            "display:block;" +
        "}" +

        ".iceburg-dropdown .iceburg-nav-link {" +
            "height:auto;" +
            "min-height:38px;" +
            "width:100%;" +
            "padding:9px 14px;" +
            "justify-content:space-between;" +
        "}" +

        ".iceburg-dropdown .iceburg-nav-item:hover > .iceburg-nav-link {" +
            "background:#2b2b2b;" +
        "}" +

        ".iceburg-dropdown .iceburg-dropdown {" +
            "top:-6px;" +
            "left:100%;" +
            "border-radius:7px;" +
        "}" +

        ".iceburg-dropdown .iceburg-dropdown.iceburg-open-left {" +
            "left:auto;" +
            "right:100%;" +
        "}" +

        ".iceburg-dropdown .iceburg-nav-arrow {" +
            "transform:rotate(-90deg);" +
        "}" +

        ".iceburg-dropdown .iceburg-nav-item:hover > .iceburg-nav-link .iceburg-nav-arrow {" +
            "transform:rotate(90deg);" +
        "}" +

        ".iceburg-account {" +
            "position:relative;" +
            "height:100%;" +
            "display:flex;" +
            "align-items:center;" +
    "margin-left:14px;" +
    "letter-spacing:1.2px" +
        "}" +

        ".iceburg-account-button {" +
            "height:100%;" +
            "display:flex;" +
            "align-items:center;" +
            "gap:7px;" +
            "padding:0 10px;" +
            "border:0;" +
            "background:transparent;" +
            "color:white;" +
            "font-family:Arial,Helvetica,sans-serif;" +
            "font-size:14px;" +
            "cursor:pointer;" +
    "white-space:nowrap;" +
    "letter-spacing:1.2px" +
        "}" +

        ".iceburg-account-button:hover {" +
            "background:#252525;" +
            "color:white;" +
        "}" +

        ".iceburg-account-arrow {" +
            "font-size:9px;" +
            "opacity:0.7;" +
            "transition:transform 0.15s ease;" +
        "}" +

        ".iceburg-account.open .iceburg-account-arrow {" +
            "transform:rotate(180deg);" +
        "}" +

        ".iceburg-account-menu {" +
            "position:absolute;" +
            "top:100%;" +
            "right:0;" +
            "min-width:180px;" +
            "background:#202020;" +
            "border:1px solid #333;" +
            "border-radius:0 0 7px 7px;" +
            "box-shadow:0 8px 20px rgba(0,0,0,0.35);" +
            "padding:5px 0;" +
            "display:none;" +
            "z-index:10002;" +
        "}" +

        ".iceburg-account.open .iceburg-account-menu {" +
            "display:block;" +
        "}" +

        ".iceburg-account-item {" +
            "display:block;" +
            "width:100%;" +
            "padding:10px 16px;" +
            "box-sizing:border-box;" +
            "border:0;" +
            "background:transparent;" +
            "color:#d7d7d7;" +
            "font-family:Arial,Helvetica,sans-serif;" +
            "font-size:14px;" +
            "text-align:left;" +
            "text-decoration:none;" +
            "cursor:pointer;" +
        "}" +

        ".iceburg-account-item:hover {" +
            "background:#2b2b2b;" +
            "color:white;" +
        "}" +

        ".iceburg-account-logout {" +
            "color:#ef6b6b;" +
        "}" +

        ".iceburg-account-logout:hover {" +
            "color:#ff8585;" +
        "}";

    document.head.appendChild(style);


    const header = document.createElement("header");
    header.className = "iceburg-header";


    /*
     * ------------------------------------------------------------
     * LOGO
     * ------------------------------------------------------------
     */

    const logoContainer = document.createElement("div");
    logoContainer.className = "iceburg-logo-container";

    const logo = document.createElement("img");
        logo.src = "/Iceburg Log.png";
    logo.alt = "Iceburg";

    const logoText = document.createElement("div");
    logoText.className = "iceburg-logo-text";
    logoText.textContent = "Iceburg";

    logoContainer.appendChild(logo);
    logoContainer.appendChild(logoText);

    logoContainer.addEventListener("click", function () {
        window.location.href = "/dashboard.html";
    });

    header.appendChild(logoContainer);


    /*
     * ------------------------------------------------------------
     * NAVIGATION
     * ------------------------------------------------------------
     */

    const nav = document.createElement("nav");
    nav.className = "iceburg-nav";

    header.appendChild(nav);


    /*
     * ------------------------------------------------------------
     * DROPDOWN POSITIONING
     * ------------------------------------------------------------
     */

        function positionDropdown(dropdown, parentItem) {
            if (!dropdown || !parentItem) return;

            dropdown.classList.remove("iceburg-open-left");
            dropdown.style.marginTop = "";

            const parentRect = parentItem.getBoundingClientRect();
            const dropdownWidth = dropdown.offsetWidth || 190;

            const rightSpace = window.innerWidth - parentRect.right;
            const leftSpace = parentRect.left;

            if (rightSpace < dropdownWidth && leftSpace >= dropdownWidth) {
                dropdown.classList.add("iceburg-open-left");
            }

            const dropdownRect = dropdown.getBoundingClientRect();

            if (dropdownRect.bottom > window.innerHeight) {
                const overflow =
                    dropdownRect.bottom - window.innerHeight + 10;

                dropdown.style.marginTop =
                    "-" + overflow + "px";
            }

            const updatedRect = dropdown.getBoundingClientRect();

            if (updatedRect.top < 10) {
                const correction =
                    10 - updatedRect.top;

                dropdown.style.marginTop =
                    correction + "px";
            }
        }


    /*
     * ------------------------------------------------------------
     * CREATE NAV ITEM
     * ------------------------------------------------------------
     */

    function createNavItem(item) {

        if (!item || !item.label) {
            return null;
        }

        const itemElement =
            document.createElement("div");

        itemElement.className =
            "iceburg-nav-item";


        const link =
            document.createElement("a");

        link.className =
            "iceburg-nav-link";

        link.textContent =
            item.label;


        const hasChildren =
            Array.isArray(item.children) &&
            item.children.length > 0;


        if (hasChildren) {

            link.href = "#";

            link.addEventListener(
                "click",
                function (event) {
                    event.preventDefault();
                }
            );


            const arrow =
                document.createElement("span");

            arrow.className =
                "iceburg-nav-arrow";

            arrow.textContent =
                "▼";

            link.appendChild(arrow);

            itemElement.appendChild(link);


            const dropdown =
                document.createElement("div");

            dropdown.className =
                "iceburg-dropdown";


            item.children.forEach(
                function (child) {

                    const childElement =
                        createNavItem(child);

                    if (childElement) {
                        dropdown.appendChild(
                            childElement
                        );
                    }
                }
            );


            itemElement.appendChild(dropdown);


            /*
             * Position the dropdown when
             * the user moves over it.
             */

            itemElement.addEventListener(
                "mouseenter",
                function () {

                    setTimeout(
                        function () {

                            positionDropdown(
                                dropdown,
                                itemElement
                            );

                        },
                        0
                    );
                }
            );

        } else {

            if (item.url) {
                link.href = item.url;
            } else {
                link.href = "#";
            }

            itemElement.appendChild(link);
        }


        return itemElement;
    }


    /*
     * ------------------------------------------------------------
     * LOAD NAVIGATION
     * ------------------------------------------------------------
     */

    let lastNavigationJson = null;


    async function loadNavigation() {

        try {

            const response =
                await fetch(
                    "/api/nav",
                    {
                        credentials:
                            "same-origin",
                        cache:
                            "no-store"
                    }
                );


            /*
             * Unauthorized.
             */

            if (response.status === 401) {

                const returnUrl =
                    window.location.pathname +
                    window.location.search +
                    window.location.hash;

                window.location.href =
                    "/login.html?returnUrl=" +
                    encodeURIComponent(
                        returnUrl
                    );

                return;
            }


            if (!response.ok) {
                return;
            }


            const navigation =
                await response.json();


            if (!Array.isArray(navigation)) {
                return;
            }


            /*
             * Convert the navigation to a string.
             *
             * If it is identical to the previous
             * response, DO NOT touch the DOM.
             *
             * This is what keeps dropdowns open.
             */

            const navigationJson =
                JSON.stringify(navigation);


            if (
                lastNavigationJson ===
                navigationJson
            ) {

                return;
            }


            /*
             * Remember the new navigation.
             */

            lastNavigationJson =
                navigationJson;


            /*
             * The navigation actually changed,
             * so rebuild it.
             */

            nav.replaceChildren();


            navigation.forEach(
                function (item) {

                    const navItem =
                        createNavItem(item);

                    if (navItem) {
                        nav.appendChild(
                            navItem
                        );
                    }
                }
            );

        } catch (error) {

            console.error(
                "Failed to load navigation:",
                error
            );
        }
    }


    /*
     * ------------------------------------------------------------
     * ACCOUNT
     * ------------------------------------------------------------
     */

    const account =
        document.createElement("div");

    account.className =
        "iceburg-account";


    const accountButton =
        document.createElement("button");

    accountButton.type =
        "button";

    accountButton.className =
        "iceburg-account-button";


    const username =
        document.createElement("span");

    username.textContent =
        "User";


    const accountArrow =
        document.createElement("span");

    accountArrow.className =
        "iceburg-account-arrow";

    accountArrow.textContent =
        "▼";


    accountButton.appendChild(
        username
    );

    accountButton.appendChild(
        accountArrow
    );


    const accountMenu =
        document.createElement("div");

    accountMenu.className =
        "iceburg-account-menu";


    /*
     * Change Password
     */

    const changePassword =
        document.createElement("a");

    changePassword.className =
        "iceburg-account-item";

    changePassword.href =
        "change-password.html";

    changePassword.textContent =
            "Change Password";

        const verson =
            document.createElement("a");

        verson.className =
            "iceburg-account-item";

        verson.href =
            "";

        verson.textContent =
            "Iceburg V0.0.0";


    /*
     * Logout
     */

    const logout =
        document.createElement("button");

    logout.type =
        "button";

    logout.className =
        "iceburg-account-item iceburg-account-logout";

    logout.textContent =
        "Logout";


    logout.addEventListener(
        "click",
        async function () {

            try {

                const response =
                    await fetch(
                        "/api/logout",
                        {
                            method:
                                "POST",
                            credentials:
                                "same-origin"
                        }
                    );


                let redirectUrl =
                    "/login.html";


                try {

                    const result =
                        await response.json();

                    if (
                        result &&
                        result.redirect
                    ) {

                        redirectUrl =
                            result.redirect;
                    }

                } catch (e) {
                }


                window.location.href =
                    redirectUrl;

            } catch (error) {

                console.error(
                    "Logout failed:",
                    error
                );

                window.location.href =
                    "/login.html";
            }
        }
    );


    accountMenu.appendChild(
        changePassword
    );

    accountMenu.appendChild(
        logout
    );

        accountMenu.appendChild(
            verson
        );

    account.appendChild(
        accountButton
    );

    account.appendChild(
        accountMenu
    );

    header.appendChild(account);


    /*
     * Account dropdown.
     */

    accountButton.addEventListener(
        "click",
        function (event) {

            event.stopPropagation();

            account.classList.toggle(
                "open"
            );
        }
    );


    document.addEventListener(
        "click",
        function () {

            account.classList.remove(
                "open"
            );
        }
    );


    accountMenu.addEventListener(
        "click",
        function (event) {

            event.stopPropagation();
        }
    );


    /*
     * ------------------------------------------------------------
     * CURRENT USER
     * ------------------------------------------------------------
     */

    async function loadCurrentUser() {

        try {

            const response =
                await fetch(
                    "/api/me",
                    {
                        credentials:
                            "same-origin",
                        cache:
                            "no-store"
                    }
                );


            if (response.status === 401) {

                const returnUrl =
                    window.location.pathname +
                    window.location.search +
                    window.location.hash;

                window.location.href =
                    "/login.html?returnUrl=" +
                    encodeURIComponent(
                        returnUrl
                    );

                return;
            }


            if (!response.ok) {
                return;
            }


            const data =
                await response.json();


            if (
                data &&
                data.username
            ) {

                username.textContent =
                    data.username;
            }
            if (
                data &&
                data.verson
            ) {

                verson.textContent =
                    data.verson;
            }

        } catch (error) {

            console.error(
                "Failed to load current user:",
                error
            );
        }
    }


    /*
     * ------------------------------------------------------------
     * INSERT HEADER
     * ------------------------------------------------------------
     */

    if (document.body.firstChild) {

        document.body.insertBefore(
            header,
            document.body.firstChild
        );

    } else {

        document.body.appendChild(
            header
        );
    }


    /*
     * ------------------------------------------------------------
     * INITIAL LOAD
     * ------------------------------------------------------------
     */

    loadNavigation();
    loadCurrentUser();


    /*
     * Check the server every 3 seconds.
     *
     * The DOM is only rebuilt if the navigation
     * data has actually changed.
     */

    setInterval(
        loadNavigation,
        3000
    );
}


if (
    document.readyState ===
    "loading"
) {

    document.addEventListener(
        "DOMContentLoaded",
        initializeHeader
    );

} else {

    initializeHeader();
}


    }) ();
