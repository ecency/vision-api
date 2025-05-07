import React from "react";
import {hydrate} from "react-dom";

import App from "../common/app";

hydrate(
    <App/>,
    document.getElementById("root")
);



if (module.hot) {
    module.hot.accept("../common/app", () => {
        hydrate(
            <App/>,
            document.getElementById("root")
        );
    });
}
