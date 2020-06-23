import React, { Component } from "react";

import { User } from "../../store/users/types";
import { ActiveUser } from "../../store/active-user/types";

import Login from "../login";

import { getAuthUrl } from "../../helper/hive-signer";

interface Props {
  users: User[];
  activeUser: ActiveUser | null;
  children: JSX.Element;
  setActiveUser: (name?: string) => void;
}

interface State {
  loginVisible: boolean;
}

export default class LoginRequired extends Component<Props, State> {
  state: State = {
    loginVisible: false,
  };

  toggle = () => {
    const { loginVisible } = this.state;
    this.setState({ loginVisible: !loginVisible });

    // window.location.href = getAuthUrl();
  };

  render() {
    const { children, activeUser } = this.props;
    const { loginVisible } = this.state;

    if (activeUser) {
      return children;
    }

    const clonedChildren = React.cloneElement(children, {
      onClick: this.toggle,
    });

    return (
      <>
        {clonedChildren}
        {loginVisible && <Login {...this.props} onHide={this.toggle} onLogin={this.toggle} />}
      </>
    );
  }
}
